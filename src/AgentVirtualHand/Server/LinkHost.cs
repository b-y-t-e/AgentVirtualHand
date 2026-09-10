using System.Net.Http.Headers;
using System.Text;
using Tailcat.Link;

namespace AgentVirtualHand.Server;

/// <summary>Maszyna kliencka widziana przez hosta: tożsamość z linku plus stan połączenia.</summary>
public sealed record PeerInfo(string Key, string Name, bool IsConnected, DateTimeOffset PairedAt);

/// <summary>
/// Wystawia maszynę przez Tailcat.Link zamiast bezpośredniego połączenia po IP.
/// Obsługuje wielu klientów naraz - każdy ma własne okno dostępu, więc odcięcie
/// jednego nie rusza pozostałych. Żądania trafiają do lokalnego serwera HTTP na
/// 127.0.0.1, dzięki czemu cała logika API zostaje jedna, a link jest tylko transportem.
/// </summary>
public sealed class LinkHost : IAsyncDisposable
{
    private const string AppName = "agentvirtualhand";

    private readonly SessionManager _sessions;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private ILinkHost? _host;
    private LinkInvitation? _invitation;

    public LinkHost(SessionManager sessions) => _sessions = sessions;

    public event Action<string, string>? Audit;
    public event Action? Changed;

    /// <summary>Maszyna wlasnie sie polaczyla - tryb bez okna otwiera jej dostep od razu.</summary>
    public event Action<PeerInfo>? PeerJoined;

    public bool IsHosting => _host is not null;

    /// <summary>
    /// Kod aktualnego zaproszenia albo pusty, gdy zadne nie czeka na uzycie.
    /// Znika sam, gdy maszyna go uzyje - bo zaproszenie jest jednorazowe.
    /// </summary>
    public string InvitationCode => _invitation?.Code.Value ?? "";

    public DateTimeOffset? InvitationExpiresAt => _invitation?.ExpiresAt;

    /// <summary>Ile maszyn może być sparowanych jednocześnie - limit biblioteki.</summary>
    public int MaxPeers => _host?.MaxPeers ?? 0;

    /// <summary>Port lokalnego serwera HTTP, do którego przekazujemy żądania z linku.</summary>
    public int LoopbackPort { get; set; } = 8787;

    public IReadOnlyList<PeerInfo> Peers => _host is null
        ? []
        : _host.Peers.Select(Describe).ToList();

    /// <summary>Ile czasu maszyna ma na wpisanie kodu, zanim ten sam wygasnie.</summary>
    public static TimeSpan InvitationLifetime { get; } = TimeSpan.FromMinutes(15);

    public async Task StartAsync(int maxPeers)
    {
        if (_host is not null) throw new InvalidOperationException("Link już działa.");

        var options = new LinkOptions
        {
            PairingWindow = InvitationLifetime,
            MaxPeers = maxPeers,
            Store = new HiddenLinkStore(),
            Log = message => Audit?.Invoke("link", message),
        };

        var host = await TailcatLink.HostManyAsync(AppName, options).ConfigureAwait(false);
        host.SetRequestHandler(HandleAsync);

        host.PeerJoined += (_, e) =>
        {
            // Kod byl jednorazowy i wlasnie zostal zuzyty - nie ma sensu dalej go pokazywac.
            _invitation = null;

            Audit?.Invoke("link", $"{Name(e.Peer)}: dołączyła kodem zaproszenia");
            PeerJoined?.Invoke(Describe(e.Peer));
            Changed?.Invoke();
        };
        host.PeerLeft += (_, e) =>
        {
            Audit?.Invoke("link", $"{Name(e.Peer)}: rozłączona ({e.Reason})");
            Changed?.Invoke();
        };

        _host = host;

        // Zadnego kodu na starcie: kod pojawia sie dopiero, gdy operator zaprasza maszyne,
        // i znika, gdy zostanie uzyty. Inaczej okno pokazywaloby martwy kod z poprzedniej sesji.
        Audit?.Invoke("link", "Link gotowy");
        Changed?.Invoke();
    }

    /// <summary>
    /// Nowe zaproszenie dla kolejnej maszyny. Jednorazowe, więc jeden kod wpuszcza jedną maszynę
    /// i nie da się go użyć powtórnie, gdy trafi w niepowołane ręce.
    /// </summary>
    public async Task<LinkInvitation> InviteAsync()
    {
        if (_host is null) throw new InvalidOperationException("Link nie działa.");

        var invitation = await _host.InviteAsync(new InvitationRequest
        {
            Lifetime = InvitationLifetime,
            SingleUse = true,
        }).ConfigureAwait(false);

        _invitation = invitation;

        Audit?.Invoke("link", $"Kod zaproszenia ważny do {invitation.ExpiresAt.LocalDateTime:HH:mm}");
        Changed?.Invoke();
        return invitation;
    }

    /// <summary>Kasuje niewykorzystany kod - po zatrzymaniu linku i przy wystawianiu nowego.</summary>
    public void DropInvitation() => _invitation = null;

    /// <summary>Odpina maszynę: traci dostęp i przy powrocie musi dostać nowy kod.</summary>
    public async Task ForgetPeerAsync(string peerKey)
    {
        if (_host is null) return;
        if (_host.Peers.FirstOrDefault(p => p.Key.ToString() == peerKey) is not { } peer) return;

        _sessions.Revoke(peerKey, "maszyna odpięta");
        await _host.ForgetPeerAsync(peer).ConfigureAwait(false);

        Audit?.Invoke("link", $"{Name(peer)}: odpięta, powrót wymaga nowego kodu");
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        if (_host is null) return;

        var host = _host;
        _host = null;
        _invitation = null;
        await host.DisposeAsync().ConfigureAwait(false);

        Audit?.Invoke("link", "Link zatrzymany");
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _http.Dispose();
    }

    /// <summary>
    /// Sparowanie potwierdza tożsamość maszyny, ale nie wystarcza: dostęp musi być
    /// otwarty w oknie aplikacji, osobno dla każdej maszyny, i wygasa razem z sesją.
    /// </summary>
    private async Task<ReadOnlyMemory<byte>> HandleAsync(ILinkPeer peer, ReadOnlyMemory<byte> request, CancellationToken ct)
    {
        LinkRequest call;
        try
        {
            call = LinkCodec.Decode<LinkRequest>(request);
        }
        catch (Exception ex)
        {
            return LinkCodec.Encode(LinkResponse.Error(400, $"Niepoprawna koperta: {ex.Message}"));
        }

        var session = _sessions.ForPeer(peer.Key.ToString());
        if (session is null)
            return LinkCodec.Encode(LinkResponse.Error(401,
                "Dostęp zamknięty - właściciel maszyny musi go otworzyć w oknie AVH."));

        try
        {
            var response = await ForwardAsync(call, session.Token, ct).ConfigureAwait(false);
            return LinkCodec.Encode(response);
        }
        catch (Exception ex)
        {
            Audit?.Invoke("deny", $"{Name(peer)}: błąd obsługi żądania - {ex.Message}");
            return LinkCodec.Encode(LinkResponse.Error(500, ex.Message));
        }
    }

    private async Task<LinkResponse> ForwardAsync(LinkRequest call, string token, CancellationToken ct)
    {
        var path = call.Path.StartsWith('/') ? call.Path : "/" + call.Path;
        var url = $"http://127.0.0.1:{LoopbackPort}{path}";
        if (!string.IsNullOrEmpty(call.Query)) url += "?" + call.Query;

        using var message = new HttpRequestMessage(new HttpMethod(call.Method), url);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (call.BodyBase64 is { Length: > 0 })
        {
            message.Content = new ByteArrayContent(Convert.FromBase64String(call.BodyBase64));
            message.Content.Headers.ContentType = new MediaTypeHeaderValue(call.ContentType ?? "application/octet-stream");
        }
        else if (call.Body is not null)
        {
            message.Content = new StringContent(call.Body, Encoding.UTF8, call.ContentType ?? "application/json");
        }

        using var reply = await _http.SendAsync(message, ct).ConfigureAwait(false);
        var contentType = reply.Content.Headers.ContentType?.MediaType;
        var bytes = await reply.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        // Tekst wraca jako tekst - inaczej klient musialby zgadywac kodowanie przy kazdej odpowiedzi.
        var isText = contentType is null
            || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("markdown", StringComparison.OrdinalIgnoreCase);

        return new LinkResponse
        {
            Status = (int)reply.StatusCode,
            ContentType = contentType,
            Body = isText ? Encoding.UTF8.GetString(bytes) : null,
            BodyBase64 = isText ? null : Convert.ToBase64String(bytes),
        };
    }

    private static PeerInfo Describe(ILinkPeer peer) =>
        new(peer.Key.ToString(), Name(peer), peer.IsConnected, peer.PairedAt);

    private static string Name(ILinkPeer peer) =>
        string.IsNullOrWhiteSpace(peer.Name) ? "nieznana maszyna" : peer.Name;
}
