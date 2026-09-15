using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentVirtualHand.Server;

public sealed record ExecResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    long DurationMs,
    string Shell,
    string WorkingDirectory);

/// <summary>Długo działający proces, z ktorego klient odpytuje output przyrostowo.</summary>
public sealed class ManagedProcess
{
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly object _lock = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required string Id { get; init; }
    public required Process Process { get; init; }
    public required string Command { get; init; }
    public required string WorkingDirectory { get; init; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? ExitedAt { get; private set; }
    public int? ExitCode { get; private set; }

    /// <summary>Konczy sie, gdy proces zakończy działanie - pozwala na long-poll bez odpytywania w pętli.</summary>
    public Task Exited => _exited.Task;

    public void AppendStdout(string line) { lock (_lock) _stdout.AppendLine(line); }
    public void AppendStderr(string line) { lock (_lock) _stderr.AppendLine(line); }

    public void MarkExited(int exitCode)
    {
        lock (_lock)
        {
            ExitCode = exitCode;
            ExitedAt = DateTimeOffset.Now;
        }
        _exited.TrySetResult();
    }

    /// <summary>Zwraca output od podanego offsetu (w znakach) razem z nowym offsetem.</summary>
    public (string Stdout, string Stderr, int OutOffset, int ErrOffset) ReadFrom(int outOffset, int errOffset)
    {
        lock (_lock)
        {
            var o = outOffset < 0 || outOffset > _stdout.Length ? 0 : outOffset;
            var e = errOffset < 0 || errOffset > _stderr.Length ? 0 : errOffset;
            var outChunk = _stdout.ToString(o, _stdout.Length - o);
            var errChunk = _stderr.ToString(e, _stderr.Length - e);
            return (outChunk, errChunk, _stdout.Length, _stderr.Length);
        }
    }
}

/// <summary>
/// Uruchamianie poleceń powłoki - PowerShell na Windows, bash na Linux/macOS.
/// </summary>
public sealed class ShellRunner
{
    private readonly ConcurrentDictionary<string, ManagedProcess> _processes = new();

    /// <summary>Polecenia procesow w tle, po id - ich pliki skryptow trzeba skasowac takze przy zamykaniu hosta.</summary>
    private readonly ConcurrentDictionary<string, PreparedCommand> _backgroundCommands = new();

    /// <summary>Ile KillAll czeka na zabity proces, zeby powloka puscila plik skryptu przed jego skasowaniem.</summary>
    private static readonly TimeSpan KilledProcessExitWait = TimeSpan.FromSeconds(2);

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static string DefaultShell => ShellDialect.DefaultShellName;

    public IReadOnlyCollection<ManagedProcess> Processes => _processes.Values.ToArray();

    /// <summary>Uruchamia polecenie i czeka na wynik; przejmuje <paramref name="command"/> i zwalnia go po zakonczeniu.</summary>
    public async Task<ExecResult> RunAsync(
        PreparedCommand command,
        string? workingDirectory,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var ownedCommand = command;
        var cwd = ResolveWorkingDirectory(workingDirectory);
        var psi = BuildStartInfo(command, cwd);

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.Start();
        var output = PumpOutputLines(process, line => AppendLine(stdout, line), line => AppendLine(stderr, line));

        var timedOut = false;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600)));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            TryKill(process);
        }

        await WaitForOutputAsync(output);
        sw.Stop();

        return new ExecResult(
            ExitCode: timedOut ? -1 : process.ExitCode,
            Stdout: Snapshot(stdout),
            Stderr: Snapshot(stderr),
            TimedOut: timedOut,
            DurationMs: sw.ElapsedMilliseconds,
            Shell: psi.FileName,
            WorkingDirectory: cwd);
    }

    /// <summary>Startuje proces w tle; przejmuje <paramref name="command"/> i zwalnia go, gdy proces sie zakonczy.</summary>
    public ManagedProcess Start(PreparedCommand command, string? workingDirectory)
    {
        try
        {
            var managed = StartManaged(command, workingDirectory);
            _backgroundCommands[managed.Id] = command;
            _ = managed.Exited.ContinueWith(_ => ReleaseBackgroundCommand(managed.Id), TaskScheduler.Default);
            return managed;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    private ManagedProcess StartManaged(PreparedCommand command, string? workingDirectory)
    {
        var cwd = ResolveWorkingDirectory(workingDirectory);
        var psi = BuildStartInfo(command, cwd);
        psi.RedirectStandardInput = true;
        // Bez tego .NET koduje stdin strona kodowa konsoli hosta (np. OEM 852), a powloka czeka na UTF-8.
        psi.StandardInputEncoding = Utf8NoBom;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var managed = new ManagedProcess
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            Process = process,
            Command = command.Command,
            WorkingDirectory = cwd
        };

        process.Start();
        var output = PumpOutputLines(process, managed.AppendStdout, managed.AppendStderr);
        _ = MarkExitedAfterOutputDrainsAsync(process, managed, output);

        _processes[managed.Id] = managed;
        return managed;
    }

    /// <summary>Czekamy na ostatnie linie stdout/stderr, zeby long-poll nie oddal running=false bez konca wyjscia.</summary>
    private static async Task MarkExitedAfterOutputDrainsAsync(Process process, ManagedProcess managed, Task output)
    {
        await process.WaitForExitAsync();
        await WaitForOutputAsync(output);
        managed.MarkExited(ExitCodeOf(process));
    }

    /// <summary>
    /// Czekanie na koniec potokow jest ograniczone: proces-wnuk (np. "nohup srv &amp;", "start /b") moze
    /// trzymac odziedziczony potok otwarty w nieskonczonosc, choc proces glowny juz sie zakonczyl.
    /// </summary>
    private static async Task WaitForOutputAsync(Task output)
    {
        try { await output.WaitAsync(TimeSpan.FromSeconds(ExecLimits.OutputDrainGraceSeconds)); }
        catch (TimeoutException) { /* potok trzyma wnuk */ }
    }

    private static int ExitCodeOf(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    public ManagedProcess? Get(string id) => _processes.GetValueOrDefault(id);

    public bool Kill(string id)
    {
        if (!_processes.TryGetValue(id, out var managed)) return false;
        TryKill(managed.Process);
        return true;
    }

    /// <summary>Zabija procesy w tle i od razu kasuje ich skrypty - kontynuacja po Exited moze nie zdazyc przed koncem hosta.</summary>
    public void KillAll()
    {
        foreach (var managed in _processes.Values)
        {
            TryKill(managed.Process);
            WaitForKilledExit(managed.Process);
            ReleaseBackgroundCommand(managed.Id);
        }
        _processes.Clear();
        PreparedCommand.DeleteScriptDirectory();
    }

    private void ReleaseBackgroundCommand(string id)
    {
        if (_backgroundCommands.TryRemove(id, out var command))
            command.Dispose();
    }

    private static void WaitForKilledExit(Process process)
    {
        try { process.WaitForExit(KilledProcessExitWait); }
        catch (InvalidOperationException) { /* proces nie wystartowal albo zostal juz zwolniony */ }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Proces mogl zakończyć się sam w międzyczasie.
        }
    }

    /// <summary>Czyta surowe bajty stdout/stderr i przekazuje zdekodowane linie; konczy sie, gdy oba potoki sie zamkna.</summary>
    private static Task PumpOutputLines(Process process, Action<string> onStdout, Action<string> onStderr)
        => Task.WhenAll(
            OutputLineDecoder.PumpAsync(process.StandardOutput.BaseStream, onStdout),
            OutputLineDecoder.PumpAsync(process.StandardError.BaseStream, onStderr));

    /// <summary>Wnuk trzymajacy potok moze dopisywac po uplywie czasu na oproznienie, wiec builder chroni blokada.</summary>
    private static void AppendLine(StringBuilder output, string line)
    {
        lock (output) output.AppendLine(line);
    }

    private static string Snapshot(StringBuilder output)
    {
        lock (output) return output.ToString();
    }

    /// <summary>Stdin procesu w UTF-8 bez BOM - tak jak ustawiamy wejscie powloki (chcp 65001 / [Console]::InputEncoding).</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static string ResolveWorkingDirectory(string? workingDirectory)
        => string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory)
            ? Environment.CurrentDirectory
            : Path.GetFullPath(workingDirectory);

    private static ProcessStartInfo BuildStartInfo(PreparedCommand command, string cwd)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        command.Shell.Configure(psi, command.Command);
        return psi;
    }
}
