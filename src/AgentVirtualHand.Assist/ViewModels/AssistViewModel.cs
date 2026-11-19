using System.ComponentModel;
using System.Runtime.CompilerServices;
using AgentVirtualHand.Server;
using AgentVirtualHand.Services;
using AgentVirtualHand.ViewModels;
using Avalonia.Threading;

namespace AgentVirtualHand.Assist.ViewModels;

/// <summary>
/// Minimalny ekran pomocy ("assist"): po uruchomieniu sam stawia link, sam wystawia kod i wpuszcza
/// kazda maszyne na caly czas dzialania aplikacji. Pokazuje kod, licznik i opcje autostartu.
/// Zamkniecie okna odcina wszystkich i zatrzymuje link.
/// </summary>
public sealed class AssistViewModel : INotifyPropertyChanged
{
    // Dostep "na zawsze" = do konca dzialania aplikacji. Stan zyje w pamieci, wiec zamkniecie konczy sesje.
    private static readonly TimeSpan ForeverWindow = TimeSpan.FromDays(3650);

    private readonly SessionManager _sessions = new() { SessionDuration = ForeverWindow };
    private readonly ShellRunner _shell = new();
    private readonly RemoteHttpServer _server;
    private readonly LinkHost _link;
    private readonly DispatcherTimer _timer;

    private bool _inviting;
    private string _code = "";
    private int _connected;
    private readonly AssistStartup _startup = new();
    private bool _autostartEnabled;
    private string _startupError = "";

    public AssistViewModel()
    {
        if (OperatingSystem.IsWindows())
        {
            try { _autostartEnabled = _startup.IsEnabled(); }
            catch (Exception ex) { StartupError = $"Cannot read startup setting: {ex.Message}"; }
        }

        _server = new RemoteHttpServer(_sessions, _shell);
        _link = new LinkHost(_sessions);

        // Uzycie kodu jest juz zgoda na wpuszczenie - kazda maszyna, ktora dolaczy, dostaje dostep od razu.
        _link.PeerJoined += peer => _sessions.Open(peer.Key, peer.Name);
        _sessions.Changed += () => Dispatcher.UIThread.Post(Refresh);
        _link.Changed += () => Dispatcher.UIThread.Post(Refresh);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        _ = StartAsync();
    }

    /// <summary>Kod, ktory wystarczy podac osobie pomagajacej.</summary>
    public string Code
    {
        get => _code;
        private set => Set(ref _code, value);
    }

    public bool HasCode => Code.Length > 0;

    public bool CanAutostart => AssistStartup.IsSupported;

    public bool AutostartEnabled
    {
        get => _autostartEnabled;
        set
        {
            if (value == _autostartEnabled || !OperatingSystem.IsWindows()) return;
            UpdateAutostart(value);
        }
    }

    internal bool UpdateAutostart(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            _startup.SetEnabled(enabled);
            Set(ref _autostartEnabled, enabled, nameof(AutostartEnabled));
            StartupError = "";
            return true;
        }
        catch (Exception ex)
        {
            StartupError = $"Cannot change startup setting: {ex.Message}";
            OnPropertyChanged(nameof(AutostartEnabled));
            return false;
        }
    }

    public string StartupError
    {
        get => _startupError;
        private set
        {
            if (Set(ref _startupError, value)) OnPropertyChanged(nameof(HasStartupError));
        }
    }

    public bool HasStartupError => StartupError.Length > 0;

    public string ConnectedText => _connected switch
    {
        0 => "Waiting for a connection",
        1 => "1 connected",
        _ => $"{_connected} connected",
    };

    private async Task StartAsync()
    {
        await _server.StartAsync();
        _link.LoopbackPort = _server.Port;
        await _link.StartAsync(maxPeers: 16);
        await EnsureCodeAsync();
        Refresh();
    }

    /// <summary>
    /// Pilnuje, zeby zawsze byl wazny kod. Jednorazowy kod znika po uzyciu i wygasa po 15 min -
    /// gdy go brak, wystawiamy kolejny, zeby ekran nigdy nie zostal bez kodu.
    /// </summary>
    private async Task EnsureCodeAsync()
    {
        if (_inviting || !_link.IsHosting) return;

        var soon = DateTimeOffset.Now + TimeSpan.FromMinutes(1);
        var stillValid = _link.InvitationCode.Length > 0 && _link.InvitationExpiresAt is { } exp && exp > soon;
        if (stillValid) return;

        _inviting = true;
        try { await _link.InviteAsync(); }
        catch { /* chwilowy brak linku - sprobujemy przy kolejnym ticku */ }
        finally { _inviting = false; }
    }

    private void Refresh()
    {
        if (!_inviting) _ = EnsureCodeAsync();

        Code = _link.InvitationCode;
        _connected = _sessions.Sessions.Count;

        OnPropertyChanged(nameof(HasCode));
        OnPropertyChanged(nameof(ConnectedText));
    }

    /// <summary>Aktualizacje: przycisk restartu pod licznikiem, gdy nowsza wersja jest pobrana.</summary>
    public UpdatePrompt? Updates { get; private set; }

    public void AttachUpdates(IAppUpdates updates)
    {
        Updates = new UpdatePrompt(updates);
        Updates.Failed += message =>
            StartupError = $"Update could not be installed: {message}. Close and reopen this window.";
        OnPropertyChanged(nameof(Updates));
    }

    /// <summary>Odcina wszystkich jak zamknięcie okna, potem Velopack podmienia pliki i uruchamia nową wersję.</summary>
    public Task RestartForUpdateAsync() => Updates?.RestartAsync(ShutdownBlocking) ?? Task.CompletedTask;

    /// <summary>Zamkniecie okna: odetnij wszystkich i zatrzymaj serwer. Czeka na watku roboczym (jak host).</summary>
    public void ShutdownBlocking()
    {
        _timer.Stop();
        Task.Run(async () =>
        {
            _sessions.RevokeAll("assist window closed");
            _shell.KillAll();
            await _link.StopAsync().ConfigureAwait(false);
            await _server.StopAsync().ConfigureAwait(false);
        }).Wait(TimeSpan.FromSeconds(5));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
