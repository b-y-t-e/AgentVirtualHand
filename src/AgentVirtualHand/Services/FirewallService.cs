using System.Diagnostics;
using System.Text;

namespace AgentVirtualHand.Services;

public sealed record FirewallResult(bool Success, string Message);

/// <summary>
/// Otwieranie i zamykanie portu w firewallu systemowym.
/// Windows: netsh advfirewall (wymaga podniesienia uprawnień - system pokaże UAC).
/// Linux: ufw / firewalld / iptables przez pkexec albo sudo.
/// </summary>
public static class FirewallService
{
    private const string RulePrefix = "AgentVirtualHand";

    public static string RuleName(int port) => $"{RulePrefix} {port}";

    public static Task<FirewallResult> OpenPortAsync(int port)
        => Server.ShellRunner.IsWindows ? WindowsAsync(port, open: true) : LinuxAsync(port, open: true);

    public static Task<FirewallResult> ClosePortAsync(int port)
        => Server.ShellRunner.IsWindows ? WindowsAsync(port, open: false) : LinuxAsync(port, open: false);

    private static async Task<FirewallResult> WindowsAsync(int port, bool open)
    {
        var name = RuleName(port);
        var arguments = open
            ? $"advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol=TCP localport={port} profile=private,domain"
            : $"advfirewall firewall delete rule name=\"{name}\"";

        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            UseShellExecute = true,   // wymagane, żeby zadziałał Verb=runas
            Verb = "runas",           // prośba o podniesienie uprawnień (UAC)
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return new FirewallResult(false, "Nie udało się uruchomić netsh.");

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                return new FirewallResult(false, $"netsh zwrócił kod {process.ExitCode}. Reguła może już nie istnieć.");

            return new FirewallResult(true, open
                ? $"Port {port}/TCP otwarty w firewallu (reguła \"{name}\", profil prywatny i domenowy)."
                : $"Reguła \"{name}\" usunięta z firewalla.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new FirewallResult(false, "Anulowano okno UAC - reguła nie została zmieniona.");
        }
        catch (Exception ex)
        {
            return new FirewallResult(false, "Błąd firewalla: " + ex.Message);
        }
    }

    private static async Task<FirewallResult> LinuxAsync(int port, bool open)
    {
        var elevate = Which("pkexec") ?? Which("sudo");
        if (elevate is null)
            return new FirewallResult(false, "Brak pkexec i sudo - otwórz port ręcznie.");

        string[] command;
        if (Which("ufw") is not null)
            command = [elevate, "ufw", open ? "allow" : "delete", open ? $"{port}/tcp" : "allow", $"{port}/tcp"];
        else if (Which("firewall-cmd") is not null)
            command = [elevate, "firewall-cmd", open ? $"--add-port={port}/tcp" : $"--remove-port={port}/tcp"];
        else if (Which("iptables") is not null)
            command = [elevate, "iptables", open ? "-I" : "-D", "INPUT", "-p", "tcp", "--dport", port.ToString(), "-j", "ACCEPT"];
        else
            return new FirewallResult(false, "Nie znalazłem ufw, firewalld ani iptables - otwórz port ręcznie.");

        // ufw delete wymaga innej składni niż allow, więc sklejamy argumenty osobno.
        if (!open && Which("ufw") is not null)
            command = [elevate, "ufw", "delete", "allow", $"{port}/tcp"];

        var psi = new ProcessStartInfo
        {
            FileName = command[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in command[1..]) psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var output = (stdout + stderr).Trim();
            if (process.ExitCode != 0)
                return new FirewallResult(false, $"{command[1]} zwrócił kod {process.ExitCode}: {Shorten(output)}");

            return new FirewallResult(true, open
                ? $"Port {port}/TCP otwarty ({command[1]}). {Shorten(output)}"
                : $"Port {port}/TCP zamknięty ({command[1]}). {Shorten(output)}");
        }
        catch (Exception ex)
        {
            return new FirewallResult(false, "Błąd firewalla: " + ex.Message);
        }
    }

    private static string? Which(string tool)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in paths)
        {
            try
            {
                var candidate = Path.Combine(dir, tool);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Nieprawidłowy wpis w PATH - pomijamy.
            }
        }
        return null;
    }

    private static string Shorten(string text)
    {
        var single = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return single.Length <= 120 ? single : single[..120] + "...";
    }
}
