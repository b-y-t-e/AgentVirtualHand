using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AgentVirtualHand.Hub.Services;
using AgentVirtualHand.ViewModels;
using Avalonia.Media;
using Avalonia.Threading;

namespace AgentVirtualHand.Hub.ViewModels;

public sealed record LogEntry(string Time, string Kind, string Message)
{
    public IBrush Accent => Brush.Parse(Kind switch
    {
        "deny" => "#FF7B72",
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

    public ConnectionRow(LinkConnection connection)
    {
        _connection = connection;
        _connection.Changed += () => Dispatcher.UIThread.Post(RefreshAll);
    }

    public LinkConnection Connection => _connection;
    public ConnectionEntry Entry => _connection.Entry;

    public string Name => _connection.Entry.Name;

    public string AddressText => _connection.IsOpen ? _connection.BaseUrl : "off";

    public string StatusText => (_connection.IsOpen, _connection.IsConnected) switch
    {
        (false, _) => "off",
        (true, false) => "connecting",
        (true, true) => "connected",
    };

    public IBrush StatusAccent => Brush.Parse((_connection.IsOpen, _connection.IsConnected) switch
    {
        (false, _) => "#5C6474",
        (true, false) => "#FFB454",
        (true, true) => "#63D19B",
    });

    public bool IsOpen => _connection.IsOpen;
    public bool IsConnected => _connection.IsConnected;
    public string ToggleText => _connection.IsOpen ? "Turn off" : "Turn on";

    public bool Busy
    {
        get => _busy;
        private set { _busy = value; OnPropertyChanged(); OnPropertyChanged(nameof(NotBusy)); }
    }

    public bool NotBusy => !_busy;

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
        OnPropertyChanged(nameof(ToggleText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class HubViewModel : INotifyPropertyChanged
{
    private readonly DispatcherTimer _timer;

    private string _newCode = "";
    private string _newName = "";
    private string _hint = "Paste the invite code from the machine you want to control.";

    public HubViewModel()
    {
        foreach (var entry in ConnectionStore.Load()) Add(entry, autoStart: entry.Enabled);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) =>
        {
            foreach (var row in Connections) row.RefreshAll();
            OnPropertyChanged(nameof(ConnectionsSummary));
        };
        _timer.Start();

        Log("app", $"AVH Hub - {Connections.Count} saved connections");
    }

    /// <summary>Blokada okna hasłem - hub trzyma tokeny do cudzych maszyn, więc nie może stać otworem.</summary>
    public LockViewModel Lock { get; } = new(HubPaths.Root);

    public ObservableCollection<ConnectionRow> Connections { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];

    /// <summary>Krótkie podsumowanie w pasku: ile maszyn faktycznie odpowiada.</summary>
    public string ConnectionsSummary => Connections.Count == 0
        ? "no computers"
        : $"{Connections.Count(row => row.IsConnected)} of {Connections.Count} connected";

    public bool HasConnections => Connections.Count > 0;
    public bool HasNoConnections => Connections.Count == 0;

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
        var code = NewCode.Trim();
        if (code.Length == 0)
        {
            Hint = "Paste an invite code first.";
            return;
        }

        var name = NewName.Trim();
        if (name.Length == 0) name = $"machine {Connections.Count + 1}";

        var entry = new ConnectionEntry(Guid.NewGuid().ToString("N")[..12], name, 0, Enabled: true, Token: "");
        var row = Add(entry, autoStart: false);

        Hint = $"Parowanie z \"{name}\"...";
        try
        {
            await row.Connection.StartAsync(code);
            NewCode = "";
            NewName = "";
            Hint = $"Paired with \"{name}\". Copy the prompt to give the model access to this one machine.";
        }
        catch (Exception ex)
        {
            Connections.Remove(row);
            ConnectionStore.Forget(entry);
            Hint = $"Pairing failed: {ex.Message}";
            Log("deny", $"{name}: {ex.Message}");
        }

        Save();
    }

    public async Task ToggleAsync(ConnectionRow row)
    {
        await row.ToggleAsync();
        Save();
    }

    public async Task RemoveAsync(ConnectionRow row)
    {
        await row.Connection.StopAsync();
        Connections.Remove(row);
        ConnectionStore.Forget(row.Entry);
        OnPropertyChanged(nameof(HasConnections));
        OnPropertyChanged(nameof(HasNoConnections));
        OnPropertyChanged(nameof(ConnectionsSummary));

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
        Log("pair", $"{row.Name}: skopiowano prompt");
    }

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

        var row = new ConnectionRow(connection);
        Connections.Add(row);
        OnPropertyChanged(nameof(HasConnections));
        OnPropertyChanged(nameof(HasNoConnections));
        OnPropertyChanged(nameof(ConnectionsSummary));

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

    private void Save() => ConnectionStore.Save(Connections.Select(r =>
        r.Connection.Entry with { Enabled = r.Connection.IsOpen }));

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

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }
}
