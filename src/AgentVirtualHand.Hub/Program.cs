using System.Runtime.InteropServices;
using AgentVirtualHand.Hub.Services;
using Avalonia;

namespace AgentVirtualHand.Hub;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--pair"))
            return PairAsync(args).GetAwaiter().GetResult();

        // Jedna kopia na sesje: dwa huby bilyby sie o wspolna liste polaczen i zapamietane porty.
        if (!AgentVirtualHand.Services.SingleInstance.TryAcquire("avh-hub"))
        {
            AgentVirtualHand.Services.SingleInstance.WarnAlreadyRunning(
                "AVH Hub", "AVH Hub is already running on this machine.");
            return 1;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        // Zamknięte okno = martwy proces. Nic nie może utrzymywać przy życiu otwartych
        // portów do zdalnych maszyn po zniknięciu okna.
        Environment.Exit(0);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Dodanie maszyny z wiersza poleceń: avh-hub --pair &lt;kod&gt; [--name &lt;nazwa&gt;].
    /// Sparowanie zostaje na dysku, więc okno podniesie to połączenie już bez kodu.
    /// </summary>
    private static async Task<int> PairAsync(string[] args)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) AttachConsole(-1);
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { /* brak konsoli */ }

        var code = Value(args, "--pair");
        if (code is null)
        {
            Console.WriteLine("Usage: avh-hub --pair <invite code> [--name <name>]");
            return 2;
        }

        var connections = ConnectionStore.Load();
        var name = Value(args, "--name") ?? $"machine {connections.Count + 1}";
        var entry = new ConnectionEntry(Guid.NewGuid().ToString("N")[..12], name, 0, Enabled: true, Token: "");

        await using var connection = new LinkConnection(entry);
        connection.Audit += (kind, message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {kind,-5} {message}");

        try
        {
            await connection.StartAsync(code);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Pairing failed: {ex.Message}");
            ConnectionStore.Forget(entry);
            return 1;
        }

        connections.Add(connection.Entry);
        ConnectionStore.Save(connections);

        Console.WriteLine();
        Console.WriteLine($"  Machine:  {connection.Entry.Name}");
        Console.WriteLine($"  Address:  {connection.BaseUrl}   (only while the avh-hub window runs)");
        Console.WriteLine($"  Token:    {connection.Token}");
        Console.WriteLine();
        return 0;
    }

    private static string? Value(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
