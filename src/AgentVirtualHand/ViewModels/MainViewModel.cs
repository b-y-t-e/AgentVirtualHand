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
    private readonly DispatcherTimer _timer;

    private string _port = "8787";
    private bool _lanVisible = true;
    private int _durationMinutes = 60;
    private string _pairCode = "";
    private Bitmap? _qrImage;
    private string _connectionText = "";
    private string _statusText = "Serwer zatrzymany";
    private IBrush _statusAccent = Brush.Parse("#8B95A7");
    private string _remainingText = "";
    private string _clientText = "";
    private string _hint = "Uruchom serwer, potem kliknij Paruj.";

    public MainViewModel()
    {
        _server = new RemoteHttpServer(_sessions, _shell);
        _sessions.Audit += (kind, message) => Log(kind, message);
        _server.Audit += (kind, message) => Log(kind, message);
        _sessions.Changed += () => Dispatcher.UIThread.Post(Refresh);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Log("app", $"AgentVirtualHand {AppInfo.Version} na {Environment.MachineName}");
        Refresh();
    }

    public ObservableCollection<LogEntry> Logs { get; } = [];

    public string MachineName => Environment.MachineName;

    public string Port
    {
        get => _port;
        set => Set(ref _port, value);
    }

    public bool LanVisible
    {
        get => _lanVisible;
        set { if (Set(ref _lanVisible, value)) OnPropertyChanged(nameof(VisibilityHint)); }
    }

    public string VisibilityHint => LanVisible
        ? "Widoczny w sieci lokalnej - tego używaj do zdalnej pomocy."
        : "Tylko 127.0.0.1 - połączysz się wyłącznie z tej maszyny (np. przez tunel SSH).";

    public int DurationMinutes
    {
        get => _durationMinutes;
        set
        {
            if (!Set(ref _durationMinutes, Math.Clamp(value, 5, 480))) return;
            _sessions.SessionDuration = TimeSpan.FromMinutes(_durationMinutes);
            OnPropertyChanged(nameof(DurationText));
        }
    }

    public string DurationText => DurationMinutes >= 60
        ? $"{DurationMinutes / 60} h {DurationMinutes % 60:00} min"
        : $"{DurationMinutes} min";

    public bool IsRunning => _server.IsRunning;
    public string ServerButtonText => IsRunning ? "Zatrzymaj serwer" : "Uruchom serwer";
    public bool CanPair => IsRunning && _sessions.State != AccessState.Active;
    public bool HasSession => _sessions.State == AccessState.Active;
    public bool HasPairCode => _pairCode.Length > 0;

    /// <summary>Serwer dziala, ale nie ma jeszcze kodu - pokazujemy zachete do parowania.</summary>
    public bool ShowPairPrompt => IsRunning && !HasPairCode;

    public string PairCode
    {
        get => _pairCode;
        private set
        {
            if (!Set(ref _pairCode, value)) return;
            OnPropertyChanged(nameof(HasPairCode));
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

    public string BaseUrl
    {
        get
        {
            var host = LanVisible ? NetworkInfo.PrimaryAddress() : "127.0.0.1";
            return $"http://{host}:{Port}";
        }
    }

    public async Task ToggleServerAsync()
    {
        if (_server.IsRunning)
        {
            _sessions.Revoke("serwer zatrzymany");
            await _server.StopAsync();
            PairCode = "";
            QrImage = null;
            ConnectionText = "";
            Hint = "Serwer zatrzymany - maszyna jest odcięta.";
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
                await _server.StartAsync(new ServerOptions(port, LanVisible));
                Hint = "Serwer działa. Kliknij Paruj, żeby wpuścić klienta.";
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

    public void StartPairing()
    {
        if (!CanPair) return;

        try
        {
            var code = _sessions.StartPairing();
            PairCode = code;

            var url = $"{BaseUrl}/pair?code={code}";
            QrImage = QrGenerator.Create(url);
            ConnectionText = $"{BaseUrl}   kod: {code}";
            ClipboardPayload = BuildPayload(BaseUrl, code);
            Hint = "Kod ważny 5 minut. Skopiuj dane i wklej je w Claude Code.";
        }
        catch (Exception ex)
        {
            Hint = ex.Message;
        }

        Refresh();
    }

    public void EndSession()
    {
        _sessions.Revoke("przerwane ręcznie z GUI");
        _shell.KillAll();
        PairCode = "";
        QrImage = null;
        ConnectionText = "";
        Hint = "Dostęp odcięty. Aby wpuścić ponownie, sparuj od nowa.";
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

    /// <summary>Otwiera albo zamyka port w firewallu systemowym (Windows netsh / Linux ufw, firewalld, iptables).</summary>
    public async Task ConfigureFirewallAsync(bool open)
    {
        if (!int.TryParse(Port, out var port) || port is < 1 or > 65535)
        {
            Hint = "Najpierw ustaw poprawny port.";
            return;
        }

        Hint = open ? "Otwieram port w firewallu..." : "Usuwam regułę z firewalla...";

        var result = open
            ? await FirewallService.OpenPortAsync(port)
            : await FirewallService.ClosePortAsync(port);

        Hint = result.Message;
        Log(result.Success ? "app" : "deny", "Firewall: " + result.Message);
    }

    /// <summary>
    /// Wołane z wątku UI przy zamykaniu okna. Zatrzymanie Kestrela musi się odbyć poza wątkiem UI -
    /// czekanie na niego wprost zakleszcza, bo kontynuacje zadań wracają na dyspozytora UI,
    /// który jest właśnie zablokowany (proces zostawał z zamkniętym oknem i żywym serwerem).
    /// </summary>
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
        OnPropertyChanged(nameof(BaseUrl));
        OnPropertyChanged(nameof(ServerButtonText));
        OnPropertyChanged(nameof(CanPair));
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(ShowPairPrompt));
    }

    private string BuildPayload(string baseUrl, string code) => $$"""
        AVH {{baseUrl}} = zdalna powloka+pliki na {{Environment.MachineName}}. Dzialasz na koncie wlasciciela; operacje destrukcyjne najpierw potwierdz.
        PAIR (kod {{code}}, jednorazowy, 5 min): curl -s -X POST {{baseUrl}}/api/pair -H "Content-Type: application/json" -d "{\"code\":\"{{code}}\",\"client\":\"claude-code\"}" -> wez "token"; potem do kazdego requestu: -H "Authorization: Bearer <token>"
        API (prefix {{baseUrl}}): GET /api/help (pelna instrukcja) | GET /api/system (os, shell, dyski, home) | GET /api/session (pozostaly czas)
        EXEC: POST /api/exec {"command":"...","cwd":"...","timeoutSeconds":120} -> exitCode/stdout/stderr/timedOut
        BG: POST /api/exec/start (te same pola) -> {"id"} | GET /api/exec/<id>?outOffset=N&errOffset=N (output przyrostowo; running, exitCode) | POST /api/exec/<id>/stdin (body=tekst) | POST /api/exec/<id>/kill
        FS: GET /api/fs/list?path= | GET /api/fs/read?path=&maxBytes= | GET /api/fs/download?path= | POST /api/fs/write {"path","content"|"contentBase64","append"?} | POST /api/fs/upload?path= (body=bajty) | POST /api/fs/mkdir {"path"} | POST /api/fs/delete {"path","recursive"?} | POST /api/fs/move {"from","to"}
        NOTY: sciezki Windows w JSON z podwojnym backslashem lub /; dlugie operacje (instalacje, kompilacje) przez BG; sesja wygasa po {{DurationText}} od sparowania -> 401 (popros o nowe parowanie); koniec pracy: POST /api/session/end.
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
