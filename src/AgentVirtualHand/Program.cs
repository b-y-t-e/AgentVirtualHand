using System.Runtime.InteropServices;
using AgentVirtualHand.Server;
using AgentVirtualHand.Services;
using Avalonia;

namespace AgentVirtualHand;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--headless"))
            return RunHeadless(args).GetAwaiter().GetResult();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Tryb bez GUI - na maszynie bez pulpitu (serwer, SSH). Wypisuje kod parowania na konsole.
    /// Użycie: AgentVirtualHand --headless [--port 8787] [--minutes 60] [--local]
    /// </summary>
    private static async Task<int> RunHeadless(string[] args)
    {
        // Aplikacja jest WinExe, więc na Windows trzeba doczepić się do konsoli wołającego.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) AttachConsole(-1);
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { /* brak konsoli */ }

        var port = ArgValue(args, "--port", 8787);
        var minutes = ArgValue(args, "--minutes", 60);
        var lanVisible = !args.Contains("--local");

        var sessions = new SessionManager { SessionDuration = TimeSpan.FromMinutes(minutes) };
        var shell = new ShellRunner();
        var server = new RemoteHttpServer(sessions, shell);

        sessions.Audit += (kind, message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {kind,-6} {message}");
        server.Audit += (kind, message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {kind,-6} {message}");

        await server.StartAsync(new ServerOptions(port, lanVisible));

        var host = lanVisible ? NetworkInfo.PrimaryAddress() : "127.0.0.1";
        var code = sessions.StartPairing();

        Console.WriteLine();
        Console.WriteLine($"  Adres:          http://{host}:{port}");
        Console.WriteLine($"  Kod parowania:  {code}   (ważny 5 minut, jednorazowy)");
        Console.WriteLine($"  Czas dostępu:   {minutes} min od sparowania");
        Console.WriteLine($"  Instrukcja:     GET http://{host}:{port}/api/help");
        Console.WriteLine();
        Console.WriteLine("  Ctrl+C kończy sesję i odcina dostęp.");
        Console.WriteLine();

        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
        await stop.Task;

        sessions.Revoke("zamknięcie trybu headless");
        await server.StopAsync();
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private static int ArgValue(string[] args, string name, int fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value)
            ? value
            : fallback;
    }
}
