using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using AgentVirtualHand.Server;
using AgentVirtualHand.Services;
using Avalonia.Media;
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

    private int _durationMinutes = 60;
    private string _connectionText = "";
    private string _statusText = "Serwer zatrzymany";
    private IBrush _statusAccent = Brush.Parse("#8B95A7");
    private string _hint = "Start the link, send the code to the other machine, then let it in.";
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

        // Przekazanie kodu jest juz decyzja o wpuszczeniu, wiec maszyna, ktora go uzyje,
        // dostaje dostep od razu. Osobne "otworz dostep" pytaloby o to samo drugi raz.
        _link.PeerJoined += peer => _sessions.Open(peer.Key, peer.Name);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        LoadSettings();
        Log("app", $"AVH {AppInfo.Version} on {Environment.MachineName}");
        Refresh();
    }

    public ObservableCollection<LogEntry> Logs { get; } = [];

    /// <summary>Sparowane maszyny. Kazda ma wlasne okno dostepu i wlasny token.</summary>
    public ObservableCollection<MachineRow> Machines { get; } = [];

    /// <summary>Blokada okna hasłem - pierwsze uruchomienie wymusza jego ustawienie.</summary>
    public LockViewModel Lock { get; } = new(AppPaths.Root);

    /// <summary>Odtwarza ustawienia z poprzedniego uruchomienia. Wywolywane raz, przed pierwszym odswiezeniem adresow.</summary>
    private void LoadSettings()
    {
        var saved = AppSettings.Load();

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

        new AppSettings(DurationMinutes: DurationMinutes).Save();
    }

    public string MachineName => Environment.MachineName;

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
    /// <summary>Kod zaproszenia do wpisania raz po drugiej stronie: avh-link join &lt;kod&gt;.</summary>
    public string InvitationCode => _link.InvitationCode;

    public bool HasInvitation => IsRunning && InvitationCode.Length > 0;

    /// <summary>Link dziala, ale zaden kod nie czeka na uzycie.</summary>
    public bool ShowInvitePrompt => IsRunning && InvitationCode.Length == 0;

    public string InvitationHint => _link.InvitationExpiresAt is { } expires
        ? $"wazny do {expires.LocalDateTime:HH:mm}, wpuszcza jedna maszyne na {DurationText}"
        : "";

    public bool HasMachines => Machines.Count > 0;
    public bool HasNoMachines => IsRunning && Machines.Count == 0;

    /// <summary>Podsumowanie w pasku: ile maszyn faktycznie pracuje.</summary>
    public string MachinesSummary
    {
        get
        {
            var working = Machines.Count(m => m.HasAccess);
            var waiting = Machines.Count - working;

            return (working, waiting) switch
            {
                (0, 0) => "nikt nie pracuje",
                (0, _) => $"{waiting} czeka na wpuszczenie",
                (_, 0) => working == 1 ? "1 machine working" : $"{working} machines working",
                _ => $"{working} pracuje, {waiting} czeka",
            };
        }
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

    public string Hint
    {
        get => _hint;
        private set => Set(ref _hint, value);
    }

    /// <summary>
    /// To, co faktycznie trzeba przeniesć na druga maszyne: sam kod zaproszenia.
    /// Instrukcja dla modelu powstaje po stronie klienta - hub buduje ja dla konkretnej
    /// maszyny wraz z jej adresem i tokenem, a avh-link ma wlasne --help.
    /// </summary>
    public string ClipboardPayload { get; private set; } = "";

    public async Task ToggleServerAsync()
    {
        if (_server.IsRunning)
        {
            _sessions.RevokeAll("link zatrzymany");
            _shell.KillAll();
            await _link.StopAsync();
            await _server.StopAsync();
            ConnectionText = "";
            ClipboardPayload = "";
            Hint = "Link stopped - machine is offline.";
        }
        else
        {
            try
            {
                await _server.StartAsync();
                _link.LoopbackPort = _server.Port;
                await _link.StartAsync(MaxMachines);
                Hint = "Link running. Invite the machine that should work here.";
            }
            catch (Exception ex)
            {
                var dump = DumpException("start linku", ex);
                Log("deny", "Could not start: " + ex.Message);
                Hint = "Start error: " + ex.Message + (dump is null ? "" : $"  |  details in: {dump}");
            }
        }

        Refresh();
    }

    /// <summary>Ile maszyn moze pracowac naraz.</summary>
    public const int MaxMachines = 16;

    /// <summary>
    /// Wystawia kod dla jednej maszyny. Przekazanie kodu jest juz decyzja o wpuszczeniu:
    /// maszyna, ktora go uzyje, dostaje dostep na ustawiony czas, a kod znika.
    /// </summary>
    public async Task InviteAsync()
    {
        if (!IsRunning) return;

        try
        {
            var invitation = await _link.InviteAsync();
            ConnectionText = invitation.Code.Value;
            ClipboardPayload = invitation.Code.Value;
            Hint = $"Code valid until {invitation.ExpiresAt.LocalDateTime:HH:mm}. "
                 + $"The machine that uses it gets access for {DurationText}.";
        }
        catch (Exception ex)
        {
            Hint = ex.Message;
        }

        Refresh();
    }

    /// <summary>
    /// Wpuszcza maszyne, ktora jest juz sparowana, ale stracila okno czasowe.
    /// Nowy kod jest potrzebny tylko dla maszyny, ktorej jeszcze nigdy tu nie bylo -
    /// przy tej tozsamosc zostala potwierdzona wczesniej.
    /// </summary>
    public void AdmitMachine(MachineRow machine)
    {
        var session = _sessions.Open(machine.Key, machine.Name);
        Hint = $"{machine.Name} works until {session.ExpiresAt:HH:mm}.";
        Refresh();
    }

    public void ExtendAccess(MachineRow machine)
    {
        _sessions.Extend(machine.Key, TimeSpan.FromMinutes(DurationMinutes));
        Hint = $"Access for {machine.Name} extended.";
        Refresh();
    }

    public void RevokeAccess(MachineRow machine)
    {
        _sessions.Revoke(machine.Key, "odciete recznie z okna");
        Hint = $"{machine.Name} lost access. Other machines keep working.";
        Refresh();
    }

    /// <summary>Odcina wszystkie maszyny naraz - przycisk paniki.</summary>
    public void RevokeEveryone()
    {
        _sessions.RevokeAll("cut off manually from the window");
        _shell.KillAll();
        Hint = "All machines cut off. The link still runs - you can grant access again.";
        Refresh();
    }

    public void NoteCopied()
    {
        Hint = "Code copied. Paste it in the hub window or pass it to avh-link join on the other machine.";
        Log("app", "Invite code copied to clipboard");
    }

    public void ShutdownBlocking()
    {
        _timer.Stop();

        Task.Run(async () =>
        {
            _sessions.RevokeAll("application closing");
            await _server.StopAsync().ConfigureAwait(false);
        }).Wait(TimeSpan.FromSeconds(5));
    }

    private void Refresh()
    {
        SyncMachines();

        var (statusText, statusAccent) = (IsRunning, Machines.Count) switch
        {
            (false, _) => ("Zatrzymany", "#8B95A7"),
            (true, 0) => ("Nikt nie pracuje", "#4C8DFF"),
            (true, 1) => ("1 maszyna pracuje", "#63D19B"),
            (true, var n) => ($"{n} machines working", "#63D19B"),
        };

        StatusText = statusText;
        StatusAccent = Brush.Parse(statusAccent);

        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(InvitationCode));
        OnPropertyChanged(nameof(HasInvitation));
        OnPropertyChanged(nameof(ShowInvitePrompt));
        OnPropertyChanged(nameof(InvitationHint));
        OnPropertyChanged(nameof(ServerButtonText));
        OnPropertyChanged(nameof(HasMachines));
        OnPropertyChanged(nameof(HasNoMachines));
        OnPropertyChanged(nameof(MachinesSummary));
    }

    /// <summary>
    /// Uzgadnia liste maszyn ze stanem linku i sesji. Istniejace wiersze sa aktualizowane
    /// w miejscu, zeby lista nie mrugala co sekunde przy odswiezaniu zegara.
    /// </summary>
    private void SyncMachines()
    {
        var peers = _link.Peers;

        for (var i = Machines.Count - 1; i >= 0; i--)
        {
            if (peers.Any(peer => peer.Key == Machines[i].Key)) continue;

            Machines.RemoveAt(i);
        }

        foreach (var peer in peers)
        {
            var session = _sessions.ForPeer(peer.Key);
            var existing = Machines.FirstOrDefault(m => m.Key == peer.Key);

            // Sparowana, rozlaczona i bez dostepu - nie ma o czym informowac, dopoki sie nie odezwie.
            if (session is null && !peer.IsConnected)
            {
                if (existing is not null) Machines.Remove(existing);
                continue;
            }

            var remaining = _sessions.RemainingFor(peer.Key);
            if (existing is null) Machines.Add(new MachineRow(peer, session, remaining));
            else existing.Update(peer, session, remaining);
        }
    }

    /// <summary>
    /// Zapisuje pełny wyjątek (ze stosem i wyjątkami wewnętrznymi) obok pliku .exe.
    /// Sam Message przy błędach gniazd nie mówi, która warstwa go zgłosiła.
    /// </summary>
    private static string? DumpException(string what, Exception ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var file = Path.Combine(dir, "avh-blad.log");

            var sb = new StringBuilder();
            sb.AppendLine(new string('=', 70));
            sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {what}");
            sb.AppendLine($"maszyna={Environment.MachineName} uzytkownik={Environment.UserName} os={Environment.OSVersion}");
            sb.AppendLine($"proces={Environment.ProcessPath} 64bit={Environment.Is64BitProcess}");
            sb.AppendLine(new string('-', 70));

            AppendException(sb, ex);

            File.AppendAllText(file, sb.ToString());
            return file;
        }
        catch
        {
            return null; // diagnostyka nie może wywrócić aplikacji
        }
    }

    /// <summary>Rozwija lancuch InnerException oraz wszystkie galezie AggregateException.</summary>
    private static void AppendException(StringBuilder sb, Exception ex, int depth = 0)
    {
        var indent = new string(' ', depth * 2);
        sb.AppendLine($"{indent}[{ex.GetType().FullName}] {ex.Message}");
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
