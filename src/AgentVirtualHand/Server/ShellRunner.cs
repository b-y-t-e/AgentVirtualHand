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

    public required string Id { get; init; }
    public required Process Process { get; init; }
    public required string Command { get; init; }
    public required string WorkingDirectory { get; init; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? ExitedAt { get; private set; }
    public int? ExitCode { get; private set; }

    public void AppendStdout(string line) { lock (_lock) _stdout.AppendLine(line); }
    public void AppendStderr(string line) { lock (_lock) _stderr.AppendLine(line); }

    public void MarkExited(int exitCode)
    {
        lock (_lock)
        {
            ExitCode = exitCode;
            ExitedAt = DateTimeOffset.Now;
        }
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

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static string DefaultShell => IsWindows ? "powershell" : "bash";

    public IReadOnlyCollection<ManagedProcess> Processes => _processes.Values.ToArray();

    public async Task<ExecResult> RunAsync(
        string command,
        string? shell,
        string? workingDirectory,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var cwd = ResolveWorkingDirectory(workingDirectory);
        var psi = BuildStartInfo(command, shell, cwd);

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

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

        sw.Stop();

        return new ExecResult(
            ExitCode: timedOut ? -1 : process.ExitCode,
            Stdout: stdout.ToString(),
            Stderr: stderr.ToString(),
            TimedOut: timedOut,
            DurationMs: sw.ElapsedMilliseconds,
            Shell: psi.FileName,
            WorkingDirectory: cwd);
    }

    public ManagedProcess Start(string command, string? shell, string? workingDirectory)
    {
        var cwd = ResolveWorkingDirectory(workingDirectory);
        var psi = BuildStartInfo(command, shell, cwd);
        psi.RedirectStandardInput = true;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var managed = new ManagedProcess
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            Process = process,
            Command = command,
            WorkingDirectory = cwd
        };

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) managed.AppendStdout(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) managed.AppendStderr(e.Data); };
        process.Exited += (_, _) =>
        {
            try { managed.MarkExited(process.ExitCode); }
            catch (InvalidOperationException) { managed.MarkExited(-1); }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _processes[managed.Id] = managed;
        return managed;
    }

    public ManagedProcess? Get(string id) => _processes.GetValueOrDefault(id);

    public bool Kill(string id)
    {
        if (!_processes.TryGetValue(id, out var managed)) return false;
        TryKill(managed.Process);
        return true;
    }

    public void KillAll()
    {
        foreach (var managed in _processes.Values)
            TryKill(managed.Process);
        _processes.Clear();
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

    private static string ResolveWorkingDirectory(string? workingDirectory)
        => string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory)
            ? Environment.CurrentDirectory
            : Path.GetFullPath(workingDirectory);

    private static ProcessStartInfo BuildStartInfo(string command, string? shell, string cwd)
    {
        var chosen = string.IsNullOrWhiteSpace(shell) ? DefaultShell : shell.Trim().ToLowerInvariant();

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        switch (chosen)
        {
            case "cmd":
                psi.FileName = "cmd.exe";
                psi.Arguments = "/d /c " + command;
                break;

            case "pwsh":
            case "powershell":
                psi.FileName = chosen == "pwsh" ? "pwsh" : "powershell.exe";
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-Command");
                // Wymuszamy UTF-8, żeby polskie znaki nie gubiły się w transporcie.
                psi.ArgumentList.Add("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " + command);
                break;

            case "sh":
                psi.FileName = "/bin/sh";
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
                break;

            default:
                psi.FileName = IsWindows ? "powershell.exe" : "/bin/bash";
                if (IsWindows)
                {
                    psi.ArgumentList.Add("-NoProfile");
                    psi.ArgumentList.Add("-NonInteractive");
                    psi.ArgumentList.Add("-ExecutionPolicy");
                    psi.ArgumentList.Add("Bypass");
                    psi.ArgumentList.Add("-Command");
                    psi.ArgumentList.Add("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " + command);
                }
                else
                {
                    psi.ArgumentList.Add("-lc");
                    psi.ArgumentList.Add(command);
                }
                break;
        }

        return psi;
    }
}
