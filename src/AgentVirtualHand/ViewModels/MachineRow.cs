using System.ComponentModel;
using System.Runtime.CompilerServices;
using AgentVirtualHand.Server;
using Avalonia.Media;

namespace AgentVirtualHand.ViewModels;

/// <summary>
/// Maszyna, która ma teraz dostęp do tego komputera. Na liście są wyłącznie takie -
/// sparowana maszyna bez okna czasowego nic nie może, więc nie ma o czym informować.
/// </summary>
public sealed class MachineRow(PeerInfo peer, RemoteSession session, TimeSpan? remaining) : INotifyPropertyChanged
{
    public string Key { get; } = peer.Key;

    private PeerInfo _peer = peer;
    private RemoteSession _session = session;
    private TimeSpan? _remaining = remaining;

    public string Name => _peer.Name;

    public string StatusText => _peer.IsConnected ? "pracuje" : "rozłączona";

    public IBrush StatusAccent => Brush.Parse(_peer.IsConnected ? "#63D19B" : "#FFB454");

    /// <summary>Ile zostało do końca dostępu - jedyna liczba, która się tu liczy.</summary>
    public string DetailText => _remaining is { } left
        ? $"zostało {(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00}  ·  do {_session.ExpiresAt:HH:mm}"
        : $"do {_session.ExpiresAt:HH:mm}";

    /// <summary>Odświeża wiersz w miejscu, żeby lista nie mrugała co sekundę.</summary>
    public void Update(PeerInfo freshPeer, RemoteSession freshSession, TimeSpan? freshRemaining)
    {
        _peer = freshPeer;
        _session = freshSession;
        _remaining = freshRemaining;

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusAccent));
        OnPropertyChanged(nameof(DetailText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
