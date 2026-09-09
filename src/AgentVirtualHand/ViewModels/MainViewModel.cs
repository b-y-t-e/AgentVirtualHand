using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using AgentVirtualHand.Server;
using AgentVirtualHand.Services;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AgentVirtualHand.ViewModels;

public sealed record LogEntry(string Time, string Kind, string Message)
{
    public IBrush Accent => Brush.Parse(Kind switch
    {
        "deny" => "#FF7B72",
        "revoke" => "#FFB454",
        "pair" => "#63D19B",
        "exec" => "#4C8DFF",
        "fs" => "#C39BFF",
        _ => "#8B95A7"
    });
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly SessionManager _sessions = new();
    private readonly ShellRunner _shell = new();
    private readonly RemoteHttpServer _server;
    private readonly LinkHost _link;
    private readonly DispatcherTimer _timer;

    private string _port = "8787";
    private int _durationMinutes = 60;
    private string _pairCode = "";
    private Bitmap? _qrImage;
    private string _connectionText = "";
    private string _statusText = "Serwer zatrzymany";
    private IBrush _statusAccent = Brush.Parse("#8B95A7");
    private string _remainingText = "";
    private string _clientText = "";
    private string _hint = "Uruchom link, przekaż kod drugiej maszynie, potem otwórz dostęp.";
    private bool _settingsLoaded;

    public MainViewModel()
    {
        _server = new RemoteHttpServer(_sessions, _shell);
        _link = new LinkHost(_sessions);
        _sessions.Audit += (kind, message) => Log(kind, message);
        _server.Audit += (kind, message) => Log(kind, message);
        _link.Audit += (kind, message) => Log(kind, message);
        _sessions.Changed += () => Dispatcher.UIThread.Post(Refresh);
        _link.Changed += () => Dispatcher.UIThread.Post(Refresh);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        LoadSettings();
        Log("app", $"AgentVirtualHand {AppInfo.Version} na {Environment.MachineName}");
        Refresh();
    }

    public ObservableCollection<LogEntry> Logs { get; } = [];

    /// <summary>Odtwarza ustawienia z poprzedniego uruchomienia. Wywolywane raz, przed pierwszym odswiezeniem adresow.</summary>
    private void LoadSettings()
    {
        var saved = AppSettings.Load();

        if (saved.Port is { Length: > 0 }) _port = saved.Port;
        if (saved.DurationMinutes is { } minutes)
        {
            _durationMinutes = Math.Clamp(minutes, 5, 480);
            _sessions.SessionDuration = TimeSpan.FromMinutes(_durationMinutes);
        }

        _settingsLoaded = true;
    }

    private void SaveSettings()
    {
        if (!_settingsLoaded) return;

        new AppSettings(Port, DurationMinutes).Save();
    }

    public string MachineName => Environment.MachineName;

    public string Port
    {
        get => _port;
        set { if (Set(ref _port, value)) SaveSettings(); }
    }

    public int DurationMinutes
    {
        get => _durationMinutes;
        set
        {
            if (!Set(ref _durationMinutes, Math.Clamp(value, 5, 480))) return;
            _sessions.SessionDuration = TimeSpan.FromMinutes(_durationMinutes);
            OnPropertyChanged(nameof(DurationText));
            SaveSettings();
        }
    }

    public string DurationText => DurationMinutes >= 60
        ? $"{DurationMinutes / 60} h {DurationMinutes % 60:00} min"
        : $"{DurationMinutes} min";

    public bool IsRunning => _server.IsRunning;
    public string ServerButtonText => IsRunning ? "Zatrzymaj link" : "Uruchom link";
    public bool CanPair => IsRunning && _sessions.State != AccessState.Active;
    public bool HasSession => _sessions.State == AccessState.Active;
    /// <summary>Kod zaproszenia do wpisania raz po drugiej stronie: avh-link join &lt;kod&gt;.</summary>
    public string InvitationCode => _link.InvitationCode;

    public bool HasInvitation => InvitationCode.Length > 0;

    /// <summary>Link działa, ale dostęp nie jest jeszcze otwarty.</summary>
    public bool ShowPairPrompt => IsRunning && !HasSession;

    public bool LinkConnected => _link.IsConnected;

    public string PeerText => _link.IsConnected ? "połączona" : "czeka na drugą maszynę";

    public string PairCode
    {
        get => _pairCode;
        private set
        {
            if (!Set(ref _pairCode, value)) return;
            OnPropertyChanged(nameof(ShowPairPrompt));
        }
    }

    public Bitmap? QrImage
    {
        get => _qrImage;
        private set => Set(ref _qrImage, value);
    }

    public string ConnectionText
    {
        get => _connectionText;
        private set => Set(ref _connectionText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public IBrush StatusAccent
    {
        get => _statusAccent;
        private set => Set(ref _statusAccent, value);
    }

    public string RemainingText
    {
        get => _remainingText;
        private set => Set(ref _remainingText, value);
    }

    public string ClientText
    {
        get => _clientText;
        private set => Set(ref _clientText, value);
    }

    public string Hint
    {
        get => _hint;
        private set => Set(ref _hint, value);
    }

    /// <summary>Pełna instrukcja do wklejenia w Claude Code - budowana przy parowaniu.</summary>
    public string ClipboardPayload { get; private set; } = "";

    public async Task ToggleServerAsync()
    {
        if (_server.IsRunning)
        {
            _sessions.Revoke("link zatrzymany");
            await _link.StopAsync();
            await _server.StopAsync();
            PairCode = "";
            QrImage = null;
            ConnectionText = "";
            Hint = "Link zatrzymany - maszyna jest odcięta.";
        }
        else
        {
            if (!int.TryParse(Port, out var port) || port is < 1 or > 65535)
            {
                Hint = "Port musi być liczbą z zakresu 1-65535.";
                return;
            }

            // Bez wstepnego testu "czy port wolny": otwarte i zamkniete gniazdo probne
            // potrafi jeszcze trzymac port, gdy sekunde pozniej binduje sie Kestrel.
            // Zajetosc portu i tak zglosi sam start serwera.
            try
            {
                await _server.StartAsync(new ServerOptions(port));
                _link.LoopbackPort = port;
                await _link.StartAsync(InvitationWindow);
                RebuildInvitationArtifacts();
                Hint = "Link działa. Przekaż kod drugiej maszynie, potem otwórz dostęp.";
            }
            catch (Exception ex)
            {
                var dump = DumpException("start serwera", port, ex);
                var reason = Explain(port, ex);
                Log("deny", "Nie udało się wystartować: " + reason);
                Hint = reason + (dump is null ? "" : $"  |  szczegóły zapisane w: {dump}");
            }
        }

        Refresh();
    }

    /// <summary>Kod zaproszenia jest jednorazowy - po sparowaniu druga maszyna wraca bez niego.</summary>
    public static TimeSpan InvitationWindow => TimeSpan.FromMinutes(15);

    /// <summary>
    /// Otwiera okno dostępu. Sparowanie linku potwierdza tożsamość maszyny,
    /// ale wpuszczenie jej jest osobną decyzją i wygasa razem z sesją.
    /// </summary>
    public void OpenAccess()
    {
        if (!CanPair) return;

        try
        {
            var session = _sessions.OpenForLink("avh-link");
            Hint = $"Dostęp otwarty do {session.ExpiresAt:HH:mm}. Skopiuj instrukcję i wklej ją w Claude Code.";
        }
        catch (Exception ex)
        {
            Hint = ex.Message;
        }

        Refresh();
    }

    /// <summary>Nowy kod zaproszenia - stary przestaje wpuszczać kogokolwiek.</summary>
    public async Task NewInvitationAsync()
    {
        if (!IsRunning) return;

        try
        {
            await _link.RenewInvitationAsync();
            RebuildInvitationArtifacts();
            Hint = "Nowy kod zaproszenia. Stary już nie zadziała.";
        }
        catch (Exception ex)
        {
            Hint = ex.Message;
        }

        Refresh();
    }

    /// <summary>Host trzyma jedną sparowaną maszynę - żeby wpuścić inną, trzeba odpiąć poprzednią.</summary>
    public async Task ResetPeerAsync()
    {
        if (!IsRunning) return;

        try
        {
            _sessions.Revoke("odpięcie maszyny klienta");
            await _link.ResetPeerAsync(InvitationWindow);
            RebuildInvitationArtifacts();
            Hint = "Poprzednia maszyna odpięta. Przekaż nowy kod tej, którą chcesz wpuścić.";
        }
        catch (Exception ex)
        {
            Hint = ex.Message;
        }

        Refresh();
    }

    private void RebuildInvitationArtifacts()
    {
        var code = _link.InvitationCode;
        if (code.Length == 0)
        {
            QrImage = null;
            ConnectionText = "";
            ClipboardPayload = "";
            return;
        }

        QrImage = QrGenerator.Create(code);
        ConnectionText = code;
        ClipboardPayload = BuildPayload(code);
    }

    public void EndSession()
    {
        _sessions.Revoke("przerwane ręcznie z GUI");
        _shell.KillAll();
        Hint = "Dostęp odcięty. Link nadal działa - możesz otworzyć dostęp ponownie.";
        Refresh();
    }

    public void ExtendSession()
    {
        _sessions.Extend(TimeSpan.FromMinutes(DurationMinutes));
        Refresh();
    }

    public void NoteCopied()
    {
        Hint = "Dane skopiowane do schowka - wklej je w Claude Code na drugiej maszynie.";
        Log("app", "Dane połączenia skopiowane do schowka");
    }

    public void ShutdownBlocking()
    {
        _timer.Stop();

        Task.Run(async () =>
        {
            _sessions.Revoke("zamknięcie aplikacji");
            await _server.StopAsync().ConfigureAwait(false);
        }).Wait(TimeSpan.FromSeconds(5));
    }

    private void Refresh()
    {
        var state = _sessions.State;

        var (statusText, statusAccent) = (IsRunning, state) switch
        {
            (false, _) => ("Zatrzymany", "#8B95A7"),
            (true, AccessState.Active) => ("Sesja aktywna", "#63D19B"),
            (true, AccessState.Pairing) => ("Parowanie otwarte", "#FFB454"),
            _ => ("Nasluchuje - zablokowany", "#4C8DFF")
        };

        StatusText = statusText;
        StatusAccent = Brush.Parse(statusAccent);

        var session = _sessions.Session;
        if (session is not null)
        {
            var left = _sessions.Remaining ?? TimeSpan.Zero;
            RemainingText = $"{(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00}";
            ClientText = $"{session.ClientName} @ {session.ClientAddress} - {session.RequestCount} żądań";
        }
        else
        {
            RemainingText = "";
            ClientText = "";
            if (state != AccessState.Pairing && PairCode.Length > 0)
            {
                PairCode = "";
                QrImage = null;
                ConnectionText = "";
            }
        }

        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(InvitationCode));
        OnPropertyChanged(nameof(HasInvitation));
        OnPropertyChanged(nameof(LinkConnected));
        OnPropertyChanged(nameof(PeerText));
        OnPropertyChanged(nameof(ServerButtonText));
        OnPropertyChanged(nameof(CanPair));
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(ShowPairPrompt));
    }

    private string BuildPayload(string code) => $$"""
        AVH = zdalna powloka+pliki na {{Environment.MachineName}} przez Tailcat.Link (bez adresow IP i portow).
        Dzialasz na koncie wlasciciela; operacje destrukcyjne najpierw potwierdz.
        POLACZENIE (raz, kod jednorazowy wazny 15 min): avh-link join {{code}}
        Potem kod nie jest juz potrzebny - klient pamieta sparowanie. Sprawdz: avh-link status
        EXEC: avh-link exec "<polecenie>" [--cwd <sciezka>] [--timeout <sekundy>] -> exitCode/stdout/stderr
        BG (dlugie operacje: instalacje, kompilacje): avh-link bg start "<polecenie>" -> id
             avh-link bg out <id> [--out-offset N] [--err-offset N] | avh-link bg stdin <id> "<tekst>" | avh-link bg kill <id>
        PLIKI: avh-link fs list <sciezka> | fs read <sciezka> [--max-bytes N] | fs download <zdalna> <lokalna>
             fs write <sciezka> --text "<tresc>" [--append] | fs upload <lokalna> <zdalna>
             fs mkdir <sciezka> | fs delete <sciezka> [--recursive] | fs move <z> <do>
        RESZTA: avh-link system (os, shell, dyski, home) | avh-link session (pozostaly czas) | avh-link --help
        NOTY: sciezki Windows pisz z ukosnikiem / albo podwojnym backslashem; dostep wygasa po {{DurationText}}
        -> 401 (popros wlasciciela o ponowne otwarcie dostepu); koniec pracy: avh-link session end.
        """;

    /// <summary>
    /// Zapisuje pełny wyjątek (ze stosem i wyjątkami wewnętrznymi) obok pliku .exe.
    /// Sam Message przy błędach gniazd nie mówi, która warstwa go zgłosiła.
    /// </summary>
    private static string? DumpException(string what, int port, Exception ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var file = Path.Combine(dir, "AgentVirtualHand-blad.log");

            var sb = new StringBuilder();
            sb.AppendLine(new string('=', 70));
            sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {what}  port={port}");
            sb.AppendLine($"maszyna={Environment.MachineName} uzytkownik={Environment.UserName} os={Environment.OSVersion}");
            sb.AppendLine($"proces={Environment.ProcessPath} 64bit={Environment.Is64BitProcess}");
            sb.AppendLine(new string('-', 70));

            // Sonda sama binduje port, wiec domyslnie wylaczona - inaczej utrudnialaby
            // kolejna probe startu. Wlacza sie zmienna srodowiskowa AVH_DIAG=1.
            if (Environment.GetEnvironmentVariable("AVH_DIAG") == "1")
            {
                try { sb.AppendLine(SocketProbe.Diagnose(port)); }
                catch (Exception probeError) { sb.AppendLine($"test gniazd nie wykonany: {probeError.Message}"); }
                sb.AppendLine(new string('-', 70));
            }

            AppendException(sb, ex);

            File.AppendAllText(file, sb.ToString());
            return file;
        }
        catch
        {
            return null; // diagnostyka nie może wywrócić aplikacji
        }
    }

    /// <summary>Zamienia wyjątek startu serwera na komunikat dla użytkownika.</summary>
    private static string Explain(int port, Exception ex)
    {
        var socketError = FindSocketError(ex);
        return socketError switch
        {
            SocketError.AddressAlreadyInUse => $"Port {port} jest zajęty przez inny program - wybierz inny port.",
            SocketError.AccessDenied => $"System odmówił dostępu do portu {port}. Sprawdź, czy port nie jest zarezerwowany "
                                      + "(netsh int ipv4 show excludedportrange protocol=tcp) i czy nie blokuje go program ochronny.",
            _ => "Błąd startu serwera: " + ex.Message,
        };
    }

    private static SocketError? FindSocketError(Exception ex) => ex switch
    {
        SocketException se => se.SocketErrorCode,
        AggregateException agg => agg.InnerExceptions.Select(FindSocketError).FirstOrDefault(e => e is not null),
        { InnerException: { } inner } => FindSocketError(inner),
        _ => null,
    };

    /// <summary>Rozwija lancuch InnerException oraz wszystkie galezie AggregateException.</summary>
    private static void AppendException(StringBuilder sb, Exception ex, int depth = 0)
    {
        var indent = new string(' ', depth * 2);
        sb.AppendLine($"{indent}[{ex.GetType().FullName}] {ex.Message}");
        if (ex is SocketException se)
            sb.AppendLine($"{indent}    SocketErrorCode={se.SocketErrorCode} ErrorCode={se.ErrorCode} NativeErrorCode={se.NativeErrorCode}");
        if (ex.StackTrace is { } trace) sb.AppendLine(trace);
        sb.AppendLine(new string('-', 70));

        if (ex is AggregateException agg)
            foreach (var inner in agg.InnerExceptions) AppendException(sb, inner, depth + 1);
        else if (ex.InnerException is { } single)
            AppendException(sb, single, depth + 1);
    }

    private void Log(string kind, string message)
    {
        void Add()
        {
            Logs.Insert(0, new LogEntry(DateTime.Now.ToString("HH:mm:ss"), kind, message));
            while (Logs.Count > 300) Logs.RemoveAt(Logs.Count - 1);
        }

        if (Dispatcher.UIThread.CheckAccess()) Add();
        else Dispatcher.UIThread.Post(Add);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
