using AgentVirtualHand.Services;
using Avalonia;

namespace AgentVirtualHand.Assist;

internal static class Program
{
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
