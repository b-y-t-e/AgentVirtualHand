using AgentVirtualHand.Services;
using AgentVirtualHand.Server;
using Avalonia;

namespace AgentVirtualHand.Assist;

internal static class Program
{
    internal static bool StartMinimized { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Assist i zwykly host dziela tozsamosc parowania (ten sam plik stanu), wiec na maszynie
        // moze dzialac tylko jedna kopia - ten sam muteks co host ("avh-host").
        if (!SingleInstance.TryAcquire("avh-host"))
        {
            SingleInstance.WarnAlreadyRunning("Support", "Support is already running on this machine.");
            return 1;
        }

        // Read only local pairing state, before showing the window (no network wait or flash).
        try
        {
            StartMinimized = new HiddenLinkStore().LoadAsync("agentvirtualhand")
                .GetAwaiter().GetResult()?.IsPaired == true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Cannot read pairing state: {ex.Message}");
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        // Zamkniete okno = martwy proces. Nic nie moze trzymac serwera przy zyciu w tle.
        Environment.Exit(0);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
