using System.ComponentModel;
using System.Runtime.CompilerServices;
using AgentVirtualHand.Server;
using Avalonia.Media;

namespace AgentVirtualHand.ViewModels;

/// <summary>
/// Jedna sparowana maszyna na liście: własne okno dostępu, własny czas i własne przyciski.
/// Odcięcie jednej nie rusza pozostałych.
/// </summary>
public sealed class MachineRow(PeerInfo peer, RemoteSession? session, TimeSpan? remaining) : INotifyPropertyChanged
{
    public string Key { get; private set; } = peer.Key;

    public PeerInfo Peer { get; private set; } = peer;
    public RemoteSession? Session { get; private set; } = session;
    public TimeSpan? Remaining { get; private set; } = remaining;

    public string Name => Peer.Name;

    public bool HasAccess => Session is not null;

    public string StatusText => (Peer.IsConnected, HasAccess) switch
    {
        (false, _) => "rozłączona",
        (true, false) => "czeka na dostęp",
        (true, true) => "pracuje",
    };

    public IBrush StatusAccent => Brush.Parse((Peer.IsConnected, HasAccess) switch
    {
        (false, _) => "#5C6474",
        (true, false) => "#FFB454",
        (true, true) => "#63D19B",
    });

    /// <summary>Ile zostało do końca okna dostępu, albo czym maszyna jest teraz.</summary>
    public string DetailText => Remaining is { } left
        ? $"dostęp do {Session!.ExpiresAt:HH:mm}  ·  zostało {(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00}"
        : Peer.IsConnected
            ? "połączona, ale bez dostępu"
            : $"sparowana {Peer.PairedAt.LocalDateTime:dd.MM HH:mm}";

    public string AccessButtonText => HasAccess ? "Przedłuż" : "Otwórz dostęp";
    public bool CanOpen => Peer.IsConnected || HasAccess;

    /// <summary>Odświeża wiersz w miejscu - lista nie jest przebudowywana, żeby nie mrugała.</summary>
    public void Update(PeerInfo freshPeer, RemoteSession? freshSession, TimeSpan? freshRemaining)
    {
        Peer = freshPeer;
        Session = freshSession;
        Remaining = freshRemaining;

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(HasAccess));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusAccent));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(AccessButtonText));
        OnPropertyChanged(nameof(CanOpen));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
