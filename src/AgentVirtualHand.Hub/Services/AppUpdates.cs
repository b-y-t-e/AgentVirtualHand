using System.Reflection;
using Avalonia.Threading;
using Velopack;
using Velopack.Sources;

namespace AgentVirtualHand.Services;

/// <summary>Aktualizacje widziane z okna - interfejs, żeby UpdatePrompt dało się sprawdzić bez Velopacka.</summary>
public interface IAppUpdates
{
    /// <summary>Wersja działającej aplikacji, np. "0.2.3".</summary>
    string CurrentVersion { get; }

    /// <summary>Wersja pobrana i gotowa do zainstalowania; null, gdy nic nie czeka.</summary>
    string? ReadyVersion { get; }

    /// <summary>Wywoływane na wątku UI, gdy nowsza wersja jest już pobrana.</summary>
    event Action? UpdateReady;

    /// <summary>
    /// Instaluje czekającą wersję i uruchamia ją; przy powodzeniu proces się kończy.
    /// <paramref name="shutdownOnUi"/> (zamknięcie połączeń) idzie na wątku UI dopiero, gdy pakiet
    /// jest gotowy - trwające pobieranie nowszej wersji czeka się w tle, a nie z zablokowanym oknem.
    /// </summary>
    Task ApplyAndRestartAsync(Action shutdownOnUi);
}

/// <summary>
/// Automatyczne aktualizacje jak w TailSync: sprawdzenie przy starcie i co pół godziny, pobranie
/// nowszego wydania w tle, a w oknie przycisk "Restart to update". Nic nie instaluje się samo -
/// restart zrywa połączenia, więc decyduje człowiek.
/// Działa tylko w wersji zainstalowanej z Setup.exe; portable i build z IDE po prostu nie mają aktualizacji.
/// Plik jest linkowany do avh-assist (Compile Include).
/// </summary>
public sealed class AppUpdates : IAppUpdates, IDisposable
{
    /// <summary>Kanał wydań, który wypełnia deploy.py - osobny katalog na każdą aplikację.</summary>
    public const string FeedRoot = "https://greysource.eu/avh/updates/";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);

    private readonly UpdateManager? _manager;
    private readonly Lock _packageLock = new();
    private DispatcherTimer? _timer;
    private volatile UpdateInfo? _pending;
    private int _checking;

    /// <param name="channel">Podkatalog kanału: "hub" albo "assist".</param>
    public AppUpdates(string channel)
    {
        try
        {
            var manager = new UpdateManager(new SimpleWebSource(FeedRoot + channel + "/"));
            if (manager.IsInstalled) _manager = manager;
        }
        catch (Exception ex)
        {
            // Brak instalacji Velopacka (portable, dev) - aktualizacje są opcjonalne.
            System.Diagnostics.Trace.WriteLine($"Updates unavailable: {ex.Message}");
        }
    }

    /// <summary>Z Velopacka, a poza instalacją z assembly.</summary>
    public string CurrentVersion =>
        _manager?.CurrentVersion?.ToString()
        ?? Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? "dev";

    public bool IsSupported => _manager is not null;

    public string? ReadyVersion => _pending?.TargetFullRelease.Version.ToString();

    public event Action? UpdateReady;

    /// <summary>Wywołać na wątku UI, gdy Avalonia już działa.</summary>
    public void Start()
    {
        if (_manager is null || _timer is not null) return;

        _timer = new DispatcherTimer { Interval = CheckInterval };
        _timer.Tick += (_, _) => _ = Task.Run(CheckSilently);
        _timer.Start();
        _ = Task.Run(CheckSilently);
    }

    private void CheckSilently()
    {
        if (_manager is null || Interlocked.CompareExchange(ref _checking, 1, 0) != 0) return;
        try
        {
            var found = _manager.CheckForUpdates();
            var waiting = _pending?.TargetFullRelease.Version;

            // Tylko wydanie nowsze od czekającego: równe już leży na dysku, starsze byłoby krokiem wstecz.
            if (found is null || (waiting is not null && found.TargetFullRelease.Version <= waiting)) return;

            lock (_packageLock)
            {
                _manager.DownloadUpdates(found);
                _pending = found;
            }

            Dispatcher.UIThread.Post(() => UpdateReady?.Invoke());
        }
        catch (Exception ex)
        {
            // Brak sieci albo kanału - po cichu, następna próba za pół godziny.
            System.Diagnostics.Trace.WriteLine($"Update check failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    public Task ApplyAndRestartAsync(Action shutdownOnUi) => Task.Run(() =>
    {
        // Lock trzymany od czekania aż do instalacji: pobranie nowszej wersji kasuje pakiet czekającej,
        // więc instalacja w tym oknie dostałaby wydanie bez pliku na dysku. Czekamy tu, w tle.
        lock (_packageLock)
        {
            if (_manager is null || _pending is not { } update) return;

            // Połączenia zamykamy dopiero teraz - pakiet na pewno leży na dysku.
            Dispatcher.UIThread.Invoke(shutdownOnUi);
            _manager.ApplyUpdatesAndRestart(update);
        }
    });

    public void Dispose() => _timer?.Stop();
}
