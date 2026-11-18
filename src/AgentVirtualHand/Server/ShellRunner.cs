using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentVirtualHand.Server;

public sealed record ExecResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated,
    bool TimedOut,
    long DurationMs,
    string Shell,
    string WorkingDirectory);

/// <summary>Długo działający proces, z ktorego klient odpytuje output przyrostowo.</summary>
public sealed class ManagedProcess
{
    private readonly object _lock = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Potoki pisza tu bezposrednio, wiec ich odczyt nie trzyma calego obiektu procesu.</summary>
    internal BoundedOutput Stdout { get; } = new(ExecLimits.MaxOutputCharsPerStream);
    internal BoundedOutput Stderr { get; } = new(ExecLimits.MaxOutputCharsPerStream);

    public required string Id { get; init; }
    public required Process Process { get; init; }
    public required string Command { get; init; }
    public required string WorkingDirectory { get; init; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? ExitedAt { get; private set; }
    public int? ExitCode { get; private set; }

    /// <summary>Konczy sie, gdy proces zakończy działanie - pozwala na long-poll bez odpytywania w pętli.</summary>
    public Task Exited => _exited.Task;

    public void MarkExited(int exitCode)
    {
        lock (_lock)
        {
            ExitCode = exitCode;
            ExitedAt = DateTimeOffset.Now;
        }
        _exited.TrySetResult();
    }

    /// <summary>Wysyla linie na stdin; false, gdy proces sie zakonczyl albo nie przyjmuje wejscia.</summary>
    public async Task<bool> TrySendInputAsync(string text)
    {
        if (ExitedAt is not null) return false;
        try
        {
            var input = Process.StandardInput;
            await input.WriteLineAsync(text);
            await input.FlushAsync();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Zwraca output od podanego offsetu (w znakach) razem z nowym offsetem.</summary>
    public (string Stdout, string Stderr, int OutOffset, int ErrOffset, bool StdoutTruncated, bool StderrTruncated)
        ReadFrom(int outOffset, int errOffset)
    {
        var (stdout, newOut, stdoutTruncated) = Stdout.ReadFrom(outOffset);
        var (stderr, newErr, stderrTruncated) = Stderr.ReadFrom(errOffset);
        return (stdout, stderr, newOut, newErr, stdoutTruncated, stderrTruncated);
    }

    /// <summary>Wynik wypadl z historii: zwalnia bufory, a potoki trzymane przez wnuka sa juz tylko oprozniane.</summary>
    internal void DetachOutput()
    {
        Stdout.Detach();
        Stderr.Detach();
    }
}

/// <summary>
/// Uruchamianie poleceń powłoki - PowerShell na Windows, bash na Linux/macOS.
/// </summary>
public sealed class ShellRunner
{
    private readonly ConcurrentDictionary<string, ManagedProcess> _processes = new();
    private readonly object _processLock = new();
    private readonly Queue<string> _completed = new();

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

        var stdout = new BoundedOutput(ExecLimits.MaxOutputCharsPerStream);
        var stderr = new BoundedOutput(ExecLimits.MaxOutputCharsPerStream);

        process.Start();
        var output = PumpOutputLines(process, stdout, stderr);

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

        var (stdoutText, stdoutTruncated) = stdout.Snapshot();
        var (stderrText, stderrTruncated) = stderr.Snapshot();
        // Wnuk (np. "srv &") moze trzymac potok dalej - po oddaniu wyniku juz go tylko oprozniamy.
        stdout.Detach();
        stderr.Detach();
        ReleaseOutputStreams(process, output);

        return new ExecResult(
            ExitCode: timedOut ? -1 : process.ExitCode,
            Stdout: stdoutText,
            Stderr: stderrText,
            StdoutTruncated: stdoutTruncated,
            StderrTruncated: stderrTruncated,
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
            lock (_processLock)
            {
                return StartManaged(command, workingDirectory);
            }
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

        try { process.Start(); }
        catch
        {
            process.Dispose();
            throw;
        }

        _processes[managed.Id] = managed;
        _backgroundCommands[managed.Id] = command;
        _ = CompleteProcessAsync(managed);
        return managed;
    }

    /// <summary>Czekamy na ostatnie linie stdout/stderr, zeby long-poll nie oddal running=false bez konca wyjscia.</summary>
    private async Task CompleteProcessAsync(ManagedProcess managed)
    {
        var process = managed.Process;
        var output = PumpOutputLines(process, managed.Stdout, managed.Stderr);
        var exitCode = -1;
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            exitCode = ExitCodeOf(process);
            await WaitForOutputAsync(output).ConfigureAwait(false);
        }
        finally
        {
            ReleaseProcessResources(process, output);
            ReleaseBackgroundCommand(managed.Id);
            RememberCompleted(managed, exitCode);
        }
    }

    /// <summary>
    /// Zwalnia uchwyt procesu i stdin od razu, a potoki wyjscia dopiero gdy sie zamkna: wnuk
    /// (np. "srv &amp;") moze wciaz do nich pisac i nie moze dostac EPIPE przez zamkniecie z naszej strony.
    /// </summary>
    private static void ReleaseProcessResources(Process process, Task output)
    {
        process.StandardInput.BaseStream.Dispose();
        ReleaseOutputStreams(process, output);
        process.Dispose();
    }

    /// <summary>
    /// Odczytane potoki wyjscia trzeba zamknac samemu - Process.Dispose ich nie zamyka. Robimy to dopiero,
    /// gdy sie skoncza, bo wnuk moze do nich jeszcze pisac.
    /// </summary>
    private static void ReleaseOutputStreams(Process process, Task output)
    {
        var stdout = process.StandardOutput;
        var stderr = process.StandardError;
        _ = output.ContinueWith(_ =>
        {
            stdout.Dispose();
            stderr.Dispose();
        }, TaskScheduler.Default);
    }

    /// <summary>Dopisuje proces do zakonczonych i usuwa najstarszy wynik ponad limit; dzialajacych nie rusza.</summary>
    private void RememberCompleted(ManagedProcess managed, int exitCode)
    {
        lock (_processLock)
        {
            if (_processes.ContainsKey(managed.Id))
            {
                _completed.Enqueue(managed.Id);
                if (_completed.Count > ExecLimits.MaxCompletedBackgroundProcesses
                    && _processes.TryRemove(_completed.Dequeue(), out var oldest))
                    oldest.DetachOutput();
            }
            else
            {
                // Juz usuniety (KillAll) - wyniku nikt nie odczyta.
                managed.DetachOutput();
            }
            managed.MarkExited(exitCode);
        }
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
        lock (_processLock)
        {
            foreach (var managed in _processes.Values)
            {
                TryKill(managed.Process);
                WaitForKilledExit(managed.Process);
                ReleaseBackgroundCommand(managed.Id);
                managed.DetachOutput();
            }
            _processes.Clear();
            _completed.Clear();
            PreparedCommand.DeleteScriptDirectory();
        }
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

    /// <summary>Czyta surowe bajty stdout/stderr i zapisuje zdekodowane linie; konczy sie, gdy oba potoki sie zamkna.</summary>
    private static Task PumpOutputLines(Process process, BoundedOutput stdout, BoundedOutput stderr)
        => Task.WhenAll(
            OutputLineDecoder.PumpAsync(process.StandardOutput.BaseStream, stdout, MaxPendingLineBytes),
            OutputLineDecoder.PumpAsync(process.StandardError.BaseStream, stderr, MaxPendingLineBytes));

    /// <summary>Linia bez LF dluzsza niz tyle bajtow jest oddawana w kawalkach - bufor dekodera nie rosnie bez konca.</summary>
    private const int MaxPendingLineBytes = 64 * 1024;

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
