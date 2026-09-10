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

        // Twarda gwarancja: zamknięte okno = martwy proces. Nic nie może utrzymywać
        // tej aplikacji (a zwłaszcza jej serwera) przy życiu w tle.
        Environment.Exit(0);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Tryb bez GUI - na maszynie bez pulpitu (serwer, SSH). Wypisuje kod zaproszenia na konsole.
    /// Użycie: avh --headless [--minutes 60]
    /// </summary>
    private static async Task<int> RunHeadless(string[] args)
    {
        // Aplikacja jest WinExe, więc na Windows trzeba doczepić się do konsoli wołającego.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) AttachConsole(-1);
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { /* brak konsoli */ }

        var minutes = ArgValue(args, "--minutes", 60);

        var sessions = new SessionManager { SessionDuration = TimeSpan.FromMinutes(minutes) };
        var shell = new ShellRunner();
        var server = new RemoteHttpServer(sessions, shell);
        await using var link = new LinkHost(sessions);

        sessions.Audit += (kind, message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {kind,-6} {message}");
        server.Audit += (kind, message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {kind,-6} {message}");
        link.Audit += (kind, message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {kind,-6} {message}");

        await server.StartAsync();
        link.LoopbackPort = server.Port;
        await link.StartAsync(maxPeers: 16);

        // Tryb headless nie ma komu klikac "otworz dostep", wiec kazda maszyna,
        // ktora sie sparuje, dostaje okno od razu po dolaczeniu. Kod jest jednorazowy,
        // wiec po jego zuzyciu wypisujemy kolejny - inaczej druga maszyna nie mialaby jak wejsc.
        link.PeerJoined += peer =>
        {
            sessions.Open(peer.Key, peer.Name);
            _ = Task.Run(async () =>
            {
                var next = await link.InviteAsync();
                Console.WriteLine();
                Console.WriteLine($"  Kod dla kolejnej maszyny:  {next.Code.Value}");
                Console.WriteLine();
            });
        };

        var first = await link.InviteAsync();

        Console.WriteLine();
        Console.WriteLine($"  Kod zaproszenia:  {first.Code.Value}");
        Console.WriteLine("  Druga maszyna:    avh-link join <kod>   (kod jednorazowy, ważny 15 minut)");
        Console.WriteLine($"  Czas dostępu:     {minutes} min");
        Console.WriteLine("  Instrukcja:       avh-link help");
        Console.WriteLine();
        Console.WriteLine("  Ctrl+C kończy sesję i odcina dostęp.");
        Console.WriteLine();

        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
        await stop.Task;

        sessions.RevokeAll("zamknięcie trybu headless");
        await link.StopAsync();
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
