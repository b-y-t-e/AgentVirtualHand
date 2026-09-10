using System.ComponentModel;
using System.Runtime.CompilerServices;
using AgentVirtualHand.Server;
using Avalonia.Media;

namespace AgentVirtualHand.ViewModels;

/// <summary>
/// Maszyna widoczna na liście: albo pracuje w swoim oknie czasowym, albo jest połączona
/// i czeka na wpuszczenie. Sparowana, ale rozłączona i bez dostępu nie trafia tu wcale -
/// nie ma o czym informować, dopóki się nie odezwie.
/// </summary>
public sealed class MachineRow(PeerInfo peer, RemoteSession? session, TimeSpan? remaining) : INotifyPropertyChanged
{
    public string Key { get; } = peer.Key;

    private PeerInfo _peer = peer;
    private RemoteSession? _session = session;
    private TimeSpan? _remaining = remaining;

    public string Name => _peer.Name;

    public bool HasAccess => _session is not null;

    public string StatusText => (HasAccess, _peer.IsConnected) switch
    {
        (true, true) => "working",
        (true, false) => "disconnected",
        _ => "waiting to be let in",
    };

    public IBrush StatusAccent => Brush.Parse((HasAccess, _peer.IsConnected) switch
    {
        (true, true) => "#63D19B",
        (true, false) => "#FFB454",
        _ => "#8B95A7",
    });

    /// <summary>Ile zostało do końca dostępu - jedyna liczba, która się tu liczy.</summary>
    public string DetailText => (_session, _remaining) switch
    {
        (not null, { } left) => $"{(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00} left  ·  until {_session.ExpiresAt:HH:mm}",
        (not null, null) => $"until {_session.ExpiresAt:HH:mm}",
        _ => "paired earlier, access expired",
    };

    /// <summary>Odświeża wiersz w miejscu, żeby lista nie mrugała co sekundę.</summary>
    public void Update(PeerInfo freshPeer, RemoteSession? freshSession, TimeSpan? freshRemaining)
    {
        _peer = freshPeer;
        _session = freshSession;
        _remaining = freshRemaining;

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(HasAccess));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusAccent));
        OnPropertyChanged(nameof(DetailText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
