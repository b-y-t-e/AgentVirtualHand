using Tailcat.Link;

namespace AgentVirtualHand.Server;

/// <summary>Co transfer strumieniowy potrzebuje od hosta: sesji peera, pilnowania jej w trakcie i logu wlasciciela.</summary>
internal interface ITransferGate
{
    RemoteSession? SessionFor(ILinkPeer peer);

    /// <summary>Transfer trwa, dopoki trwa ta sama sesja: nowy token po ponownym wpuszczeniu to juz inna decyzja wlasciciela.</summary>
    SessionWatch Watch(RemoteSession session, CancellationToken ct);

    void Audit(string kind, ILinkPeer peer, string text);

    /// <summary>Transfer omija filtr na loopbacku, wiec notatke i operacje logujemy tu - wlasciciel widzi to samo co przy zwyklym zadaniu.</summary>
    void AuditStart(ILinkPeer peer, string? note, string operation);
}

/// <summary>Dostep zamkniety w trakcie albo przed transferem - hub dostaje 401, jak przy zwyklym zadaniu.</summary>
internal sealed class AccessClosedException() : Exception(TransferGate.AccessClosedMessage);

internal sealed class TransferGate(SessionManager sessions, Action<string, string> audit) : ITransferGate
{
    public const string AccessClosedMessage = "Access closed - the machine owner must grant it in the AVH window.";

    /// <summary>Tak czesto transfer sprawdza, czy sesja nadal obowiazuje - odciecie zatrzymuje go najpozniej po tym czasie.</summary>
    private static readonly TimeSpan SessionCheckInterval = TimeSpan.FromSeconds(1);

    public RemoteSession? SessionFor(ILinkPeer peer) => sessions.ForPeer(peer.Key.ToString());

    public SessionWatch Watch(RemoteSession session, CancellationToken ct) =>
        new(() => sessions.ForPeer(session.PeerKey)?.Token == session.Token, SessionCheckInterval, TimeProvider.System, ct);

    public void Audit(string kind, ILinkPeer peer, string text) => audit(kind, $"{LinkHost.Name(peer)}: {text}");

    public void AuditStart(ILinkPeer peer, string? note, string operation)
    {
        if (!string.IsNullOrWhiteSpace(note)) Audit("note", peer, note);
        Audit("fs", peer, operation);
    }
}
