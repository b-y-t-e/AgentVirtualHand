using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AgentVirtualHand.Hub.Services;
using AgentVirtualHand.Services;
using AgentVirtualHand.ViewModels;
using Avalonia.Media;
using Avalonia.Threading;

namespace AgentVirtualHand.Hub.ViewModels;

public sealed record LogEntry(string Time, string Kind, string Message)
{
    public IBrush Accent => Brush.Parse(Kind switch
    {
        "deny" => "#FF7B72",
        "note" => "#EBB866",
        "link" => "#4C8DFF",
        "pair" => "#63D19B",
        _ => "#8B95A7",
    });
}

/// <summary>Jedno połączenie na liście - własny port, własny token, własne sparowanie.</summary>
public sealed class ConnectionRow : INotifyPropertyChanged
{
    private readonly LinkConnection _connection;
    private bool _busy;

    public ConnectionRow(LinkConnection connection, Action onChanged)
    {
        _connection = connection;
        _connection.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            RefreshAll();
            onChanged();
        });
    }

    public LinkConnection Connection => _connection;
    public ConnectionEntry Entry => _connection.Entry;

    public string Name => _connection.Entry.Name;

    public string AddressText => _connection.IsOpen ? _connection.BaseUrl : "off";

    private bool _pairing;

    /// <summary>Pierwsze parowanie w toku - maszyna jest już na liście, ale jeszcze nie ma linku.</summary>
    public bool IsPairing
    {
        get => _pairing;
        set
        {
            if (_pairing == value) return;
            _pairing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NotBusy));
            RefreshAll();
        }
    }

    /// <summary>Jeden stan wiersza - z niego wynika etykieta, kolor i miejsce na liście.</summary>
    public RowStatus Status => _pairing ? RowStatus.Pairing : (_connection.IsOpen, _connection.IsConnected) switch
    {
        (false, _) => RowStatus.Off,
        (true, false) => RowStatus.Connecting,
        (true, true) => RowStatus.Connected,
    };

    public string StatusText => Status.ToString().ToLowerInvariant();

    public IBrush StatusAccent => Brush.Parse(Status switch
    {
        RowStatus.Connected => "#63D19B",
        RowStatus.Off => "#5C6474",
        _ => "#FFB454",
    });

    public bool IsOpen => _connection.IsOpen;
    public bool IsConnected => Status == RowStatus.Connected;

    /// <summary>Grupa na liście - parowanie liczy się jako łączenie.</summary>
    public StatusGroup Group => Status switch
    {
        RowStatus.Connected => StatusGroup.Connected,
        RowStatus.Off => StatusGroup.Off,
        _ => StatusGroup.Connecting,
    };

    /// <summary>Wyłączona maszyna ma przygaszoną nazwę, żeby żywe wiersze dało się wyłowić wzrokiem.</summary>
    public IBrush NameBrush => Brush.Parse(_connection.IsOpen || _pairing ? "#E6E9EF" : "#8B95A7");

    public bool Matches(string query) =>
        query.Length == 0
        || Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (IsOpen && AddressText.Contains(query, StringComparison.OrdinalIgnoreCase));

    public string ToggleText => _connection.IsOpen ? "Turn off" : "Turn on";

    public bool Busy
    {
        get => _busy;
        private set { _busy = value; OnPropertyChanged(); OnPropertyChanged(nameof(NotBusy)); }
    }

    public bool NotBusy => !_busy && !_pairing;

    public async Task ToggleAsync()
    {
        Busy = true;
        try
        {
            if (_connection.IsOpen) await _connection.StopAsync();
            else await _connection.StartAsync();
        }
        finally
        {
            Busy = false;
            RefreshAll();
        }
    }

    public void RefreshAll()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(AddressText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusAccent));
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(NameBrush));
        OnPropertyChanged(nameof(ToggleText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum RowStatus { Connected, Connecting, Pairing, Off }

public sealed class HubViewModel : INotifyPropertyChanged
{
    private const int MaxLogEntries = 300;

    private readonly DispatcherTimer _timer;

    private string _newCode = "";
    private string _newName = "";
    private string _hint = "Paste the invite code from the machine you want to control.";
    private string _query = "";
    private StatusGroup? _filter;
    private bool _isAddOpen;
    private bool _isLogOpen;
    private bool _logAlert;

    public HubViewModel() : this(TimeProvider.System) { }

    public HubViewModel(TimeProvider time)
    {
        Pairing = new PairingProgress(time, LinkConnection.PairingTimeout, LinkConnection.FirstPairingPhase,
            action => Dispatcher.UIThread.Post(action));
        Pairing.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PairingProgress.IsActive)) OnPropertyChanged(nameof(ShowAddPanel));
        };
        foreach (var entry in ConnectionStore.Load()) Add(entry, autoStart: entry.Enabled);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) =>
        {
            foreach (var row in Connections) row.RefreshAll();
            ApplyView();
        };
        _timer.Start();

        ApplyView();
        Log("app", $"AVH Hub - {Connections.Count} saved connections");
    }

    /// <summary>Blokada okna hasłem - hub trzyma tokeny do cudzych maszyn, więc nie może stać otworem.</summary>
    public LockViewModel Lock { get; } = new(HubPaths.Root);

    /// <summary>Wszystkie maszyny w kolejności zapisu - to idzie na dysk.</summary>
    public ObservableCollection<ConnectionRow> Connections { get; } = [];

    /// <summary>To, co widać: po filtrze i wyszukiwaniu, połączone na górze, z nagłówkami grup.</summary>
    public ConnectionListView List { get; } = new();

    /// <summary>Przyciski filtra stanu z licznościami.</summary>
    public IReadOnlyList<FilterOption> Filters { get; } = FilterOption.All();

    /// <summary>Parowanie w toku: pola zablokowane, pasek postępu i licznik w panelu dodawania.</summary>
    public PairingProgress Pairing { get; }

    public ObservableCollection<LogEntry> Logs { get; } = [];

    /// <summary>Log zawężony tym samym wyszukiwaniem co lista - wpisy zaczynają się od nazwy maszyny.</summary>
    public ObservableCollection<LogEntry> VisibleLogs { get; } = [];

    /// <summary>Krótkie podsumowanie w pasku: ile maszyn faktycznie odpowiada.</summary>
    public string ConnectionsSummary => Connections.Count == 0
        ? "no computers"
        : $"{ConnectedCount} of {Connections.Count} connected";

    public int ConnectedCount => Connections.Count(row => row.IsConnected);

    public bool HasConnections => Connections.Count > 0;
    public bool HasNoConnections => Connections.Count == 0;

    /// <summary>Są maszyny, ale filtr albo wyszukiwanie nic nie zostawiły.</summary>
    public bool HasNoMatches => Connections.Count > 0 && !List.HasRows;
    public bool HasVisible => List.HasRows;

    public string NoMatchesText => _query.Length > 0
        ? $"No computers match \"{_query}\"{FilterSuffix}."
        : $"No computers are {FilterName} right now.";

    private string FilterName => _filter?.ToString().ToLowerInvariant() ?? "listed";
    private string FilterSuffix => _filter is null ? "" : $" among {FilterName}";

    public string Query
    {
        get => _query;
        set
        {
            value ??= "";
            if (_query == value) return;
            _query = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasQuery));
            ApplyView();
            RebuildLogs();
        }
    }

    public bool HasQuery => _query.Length > 0;

    /// <summary>Wybrana grupa stanu; null to "All".</summary>
    public StatusGroup? Filter
    {
        get => _filter;
        set
        {
            if (_filter == value) return;
            _filter = value;
            OnPropertyChanged();
            ApplyView();
        }
    }

    /// <summary>Formularz dodawania jest schowany, gdy lista już coś ma - wcześniej zabierał miejsce na stałe.</summary>
    public bool IsAddOpen
    {
        get => _isAddOpen;
        set
        {
            if (_isAddOpen == value) return;
            _isAddOpen = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowAddPanel));
            OnPropertyChanged(nameof(AddToggleText));
        }
    }

    public bool ShowAddPanel => _isAddOpen || HasNoConnections || Pairing.IsActive;

    public string AddToggleText => _isAddOpen ? "Cancel" : "Add computer";

    public bool IsLogOpen
    {
        get => _isLogOpen;
        set
        {
            if (_isLogOpen == value) return;
            _isLogOpen = value;
            if (value) LogAlert = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LogToggleText));
        }
    }

    public string LogToggleText => $"{(_isLogOpen ? "Hide log" : "Log")}  {VisibleLogs.Count}";

    /// <summary>Odmowa przyszła przy zwiniętym logu - przycisk logu świeci na czerwono, aż ktoś go otworzy.</summary>
    public bool LogAlert
    {
        get => _logAlert;
        private set
        {
            if (_logAlert == value) return;
            _logAlert = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LogToggleBrush));
        }
    }

    public IBrush LogToggleBrush => Brush.Parse(_logAlert ? "#FF7B72" : "#8B95A7");

    public string NewCode
    {
        get => _newCode;
        set => Set(ref _newCode, value);
    }

    public string NewName
    {
        get => _newName;
        set => Set(ref _newName, value);
    }

    public string Hint
    {
        get => _hint;
        private set => Set(ref _hint, value);
    }

    /// <summary>Dodaje maszynę na podstawie jednorazowego kodu zaproszenia z jej okna.</summary>
    public async Task AddAsync()
    {
        if (Pairing.IsActive) return;

        var code = NewCode.Trim();
        if (code.Length == 0)
        {
            Hint = "Paste an invite code first.";
            return;
        }

        var name = NewName.Trim();
        if (name.Length == 0) name = $"machine {Connections.Count + 1}";

        using var cts = new CancellationTokenSource();
        var row = BeginPairing(name, cts);
        try
        {
            await row.Connection.StartAsync(code, cts.Token);
            CompletePairing(name);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            await DiscardRowAsync(row);
            Hint = $"Pairing with \"{name}\" cancelled. The invite code may already be used - ask for a new one if it fails again.";
            Log("note", $"{name}: pairing cancelled");
        }
        catch (Exception ex)
        {
            await DiscardRowAsync(row);
            Hint = $"Pairing failed: {ex.Message}";
            Log("deny", $"{name}: {ex.Message}");
        }
        finally
        {
            EndPairing(row);
        }

        ApplyView();
        Save();
    }

    /// <summary>Wiersz "pairing" od razu na liście, licznik i etap w panelu.</summary>
    private ConnectionRow BeginPairing(string name, CancellationTokenSource cts)
    {
        var entry = new ConnectionEntry(Guid.NewGuid().ToString("N")[..12], name, 0, Enabled: true, Token: "");
        var row = Add(entry, autoStart: false);
        row.IsPairing = true;
        ApplyView();

        Pairing.Start(name, cts);
        row.Connection.Progress += OnPairingProgress;
        Hint = $"Pairing with \"{name}\"...";
        return row;
    }

    private void CompletePairing(string name)
    {
        NewCode = "";
        NewName = "";
        IsAddOpen = false;
        Hint = $"Paired with \"{name}\". Copy the prompt to give the model access to this one machine.";
    }

    private void EndPairing(ConnectionRow row)
    {
        row.Connection.Progress -= OnPairingProgress;
        row.IsPairing = false;
        Pairing.Stop();
    }

    private void OnPairingProgress(string phase) => Dispatcher.UIThread.Post(() => Pairing.ReportPhase(phase));

    /// <summary>Usuwa maszynę z listy razem z jej sparowaniem.</summary>
    private async Task DiscardRowAsync(ConnectionRow row)
    {
        await row.Connection.StopAsync();
        Connections.Remove(row);
        ConnectionStore.Forget(row.Entry);
    }

    public async Task ToggleAsync(ConnectionRow row)
    {
        await row.ToggleAsync();
        ApplyView();
        Save();
    }

    public async Task RemoveAsync(ConnectionRow row)
    {
        await DiscardRowAsync(row);
        ApplyView();

        Log("link", $"{row.Name}: removed along with its pairing");
        Hint = $"Removed \"{row.Name}\". Adding it again needs a new invite code.";
        Save();
    }

    public void RotateToken(ConnectionRow row)
    {
        row.Connection.RotateToken();
        Save();
        Hint = $"New token for \"{row.Name}\" - earlier prompts no longer work.";
    }

    public string PromptFor(ConnectionRow row) => PromptBuilder.ForConnection(row.Connection);

    public void NotePromptCopied(ConnectionRow row)
    {
        Hint = $"Prompt for \"{row.Name}\" copied - it grants access to this machine only.";
        Log("pair", $"{row.Name}: prompt copied");
    }

    public void ClearFilters()
    {
        Query = "";
        Filter = null;
    }

    /// <summary>Aktualizacje: wersja w pasku i przycisk restartu; null poza oknem aplikacji.</summary>
    public UpdatePrompt? Updates { get; private set; }

    public void AttachUpdates(IAppUpdates updates)
    {
        Updates = new UpdatePrompt(updates);
        Updates.Ready += version => Log("note", $"Version {version} is ready - restart to update");
        Updates.Failed += message =>
        {
            Hint = $"Update could not be installed: {message}. Close and reopen the hub.";
            Log("deny", $"update failed: {message}");
        };
        OnPropertyChanged(nameof(Updates));
    }

    /// <summary>
    /// Zapisuje, które maszyny były włączone (nowa wersja podniesie je sama), zamyka połączenia,
    /// a potem Velopack podmienia pliki i uruchamia hub od nowa.
    /// </summary>
    public Task RestartForUpdateAsync() => Updates?.RestartAsync(ShutdownBlocking) ?? Task.CompletedTask;

    /// <summary>Zamknięcie okna musi zamknąć wszystkie porty i linki.</summary>
    public void ShutdownBlocking()
    {
        Save();
        Task.Run(async () =>
        {
            foreach (var row in Connections.ToList()) await row.Connection.StopAsync();
        }).GetAwaiter().GetResult();
    }

    private ConnectionRow Add(ConnectionEntry entry, bool autoStart)
    {
        var connection = new LinkConnection(entry);
        connection.Audit += (kind, message) => Log(kind, message);

        var row = new ConnectionRow(connection, ApplyView);
        Connections.Add(row);

        if (autoStart)
        {
            _ = Task.Run(async () =>
            {
                try { await connection.StartAsync(); }
                catch (Exception ex) { Log("deny", $"{entry.Name}: {ex.Message}"); }
                finally { Save(); }
            });
        }

        return row;
    }

    /// <summary>Przelicza widoczną listę i liczniki filtra.</summary>
    private void ApplyView()
    {
        List.Apply(Connections, _query, _filter);
        foreach (var option in Filters) option.Update(Connections, _filter);
        NotifyCounts();
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(HasConnections));
        OnPropertyChanged(nameof(HasNoConnections));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(HasVisible));
        OnPropertyChanged(nameof(NoMatchesText));
        OnPropertyChanged(nameof(ConnectionsSummary));
        OnPropertyChanged(nameof(ShowAddPanel));
    }

    private bool LogMatches(LogEntry entry) =>
        _query.Length == 0 || entry.Message.Contains(_query, StringComparison.OrdinalIgnoreCase);

    private void RebuildLogs()
    {
        VisibleLogs.Clear();
        foreach (var entry in Logs.Where(LogMatches)) VisibleLogs.Add(entry);
        OnPropertyChanged(nameof(LogToggleText));
    }

    private void Save() => ConnectionStore.Save(Connections.Select(r =>
        r.Connection.Entry with { Enabled = r.Connection.IsOpen }));

    private void Log(string kind, string message)
    {
        void Add()
        {
            var entry = new LogEntry(DateTime.Now.ToString("HH:mm:ss"), kind, message);
            Logs.Insert(0, entry);
            if (Logs.Count > MaxLogEntries)
            {
                var dropped = Logs[^1];
                Logs.RemoveAt(Logs.Count - 1);
                VisibleLogs.Remove(dropped);
            }

            if (LogMatches(entry)) VisibleLogs.Insert(0, entry);

            if (kind == "deny" && !_isLogOpen) LogAlert = true;
            OnPropertyChanged(nameof(LogToggleText));
        }

        if (Dispatcher.UIThread.CheckAccess()) Add();
        else Dispatcher.UIThread.Post(Add);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }
}
