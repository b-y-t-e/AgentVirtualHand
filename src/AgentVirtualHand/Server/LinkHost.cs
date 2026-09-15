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

    private readonly StreamedFileTransfers _files;

    private ILinkHost? _host;
    private LinkInvitation? _invitation;

    public LinkHost(SessionManager sessions, TimeProvider? clock = null)
    {
        _sessions = sessions;
        _files = new StreamedFileTransfers(sessions, (kind, text) => Audit?.Invoke(kind, text), clock ?? TimeProvider.System);
    }

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
        if (_host is not null) throw new InvalidOperationException("Link already running.");

        var options = new LinkOptions
        {
            PairingWindow = InvitationLifetime,
            MaxPeers = maxPeers,
            Store = new HiddenLinkStore(),
            // Domyslne 15 s to za rzadko: transport zrywa lacze po ~10 s ciszy, wiec
            // bezczynny link odpadalby i wstawal co 10 s. 5 s trzyma go zywym.
            HeartbeatInterval = TimeSpan.FromSeconds(5),
            Log = message => Audit?.Invoke("link", message),
        };

        var host = await TailcatLink.HostManyAsync(AppName, options).ConfigureAwait(false);
        // Strumieniowy handler (0.5.0): koperta w metadanych, cialo jako strumien - dowolny rozmiar
        // bez limitu i bez trzymania w pamieci. Stary klient (avh-link) trafia tu z pustymi metadanymi.
        host.SetRequestHandler(HandleAsync);

        host.PeerJoined += (_, e) =>
        {
            // Kod byl jednorazowy i wlasnie zostal zuzyty - nie ma sensu dalej go pokazywac.
            _invitation = null;

            Audit?.Invoke("link", $"{Name(e.Peer)}: joined with an invite code");
            PeerJoined?.Invoke(Describe(e.Peer));
            Changed?.Invoke();
        };
        host.PeerLeft += (_, e) =>
        {
            Audit?.Invoke("link", $"{Name(e.Peer)}: disconnected ({e.Reason})");
            Changed?.Invoke();
        };

        _host = host;

        // Zadnego kodu na starcie: kod pojawia sie dopiero, gdy operator zaprasza maszyne,
        // i znika, gdy zostanie uzyty. Inaczej okno pokazywaloby martwy kod z poprzedniej sesji.
        Audit?.Invoke("link", "Link ready");
        Changed?.Invoke();
    }

    /// <summary>
    /// Nowe zaproszenie dla kolejnej maszyny. Jednorazowe, więc jeden kod wpuszcza jedną maszynę
    /// i nie da się go użyć powtórnie, gdy trafi w niepowołane ręce.
    /// </summary>
    public async Task<LinkInvitation> InviteAsync()
    {
        if (_host is null) throw new InvalidOperationException("Link is not running.");

        var invitation = await _host.InviteAsync(new InvitationRequest
        {
            Lifetime = InvitationLifetime,
            SingleUse = true,
        }).ConfigureAwait(false);

        _invitation = invitation;

        Audit?.Invoke("link", $"Invite code valid until {invitation.ExpiresAt.LocalDateTime:HH:mm}");
        Changed?.Invoke();
        return invitation;
    }

    /// <summary>Kasuje niewykorzystany kod - po zatrzymaniu linku i przy wystawianiu nowego.</summary>
    public void DropInvitation() => _invitation = null;

    /// <summary>Odpina maszynę: traci dostęp i przy powrocie musi dostać nowy kod.</summary>
    public async Task ForgetPeerAsync(string peerKey)
    {
        if (_host is null) return;

        // Revoke najpierw, zeby sesja znikla nawet gdy Tailcat juz zdjal rozlaczonego peera.
        _sessions.Revoke(peerKey, "machine deleted");

        var peer = _host.Peers.FirstOrDefault(p => p.Key.ToString() == peerKey);
        if (peer is not null) await _host.ForgetPeerAsync(peer).ConfigureAwait(false);

        Audit?.Invoke("link", $"{(peer is null ? "machine" : Name(peer))}: deleted, coming back needs a new code");
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        if (_host is null) return;

        var host = _host;
        _host = null;
        _invitation = null;
        await host.DisposeAsync().ConfigureAwait(false);

        Audit?.Invoke("link", "Link stopped");
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _http.Dispose();
    }

    /// <summary>
    /// Sparowanie potwierdza tożsamość maszyny, ale nie wystarcza: dostęp musi być otwarty w oknie
    /// aplikacji, osobno dla każdej maszyny, i wygasa razem z sesją. Dwie ścieżki: puste metadane to
    /// stary klient (avh-link) z całą <see cref="LinkRequest"/> w treści; metadane obecne to hub
    /// strumieniowy - koperta w metadanych, ciało jako strumień dowolnego rozmiaru.
    /// </summary>
    private async Task<LinkContent> HandleAsync(ILinkPeer peer, IncomingTransfer request, CancellationToken ct)
    {
        var legacy = request.Metadata.IsEmpty;
        var session = _sessions.ForPeer(peer.Key.ToString());
        if (session is null)
            return Answer(legacy, LinkResponse.Error(401, AccessClosedException.DefaultMessage));

        try
        {
            return legacy
                ? await HandleLegacyAsync(session, request, ct).ConfigureAwait(false)
                : await HandleStreamingAsync(peer, session, request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Audit?.Invoke("deny", $"{Name(peer)}: request handling error - {ex.Message}");
            return Answer(legacy, LinkResponse.Error(500, ex.Message));
        }
    }

    /// <summary>Stary klient (avh-link): cała <see cref="LinkRequest"/> z ciałem w treści, odpowiedź tak samo.</summary>
    private async Task<LinkContent> HandleLegacyAsync(RemoteSession session, IncomingTransfer request, CancellationToken ct)
    {
        LinkRequest call;
        try { call = LinkCodec.Decode<LinkRequest>(await request.ReadAllBytesAsync(ct).ConfigureAwait(false)); }
        catch (Exception ex) { return LinkWire.LegacyResponse(LinkResponse.Error(400, $"Malformed envelope: {ex.Message}")); }

        var reply = await ForwardAsync(call, LinkWire.BodyBytes(call), session.Token, ct).ConfigureAwait(false);
        return LinkWire.LegacyResponse(reply);
    }

    /// <summary>Hub: koperta w metadanych, ciało jako strumień. Pliki strumieniem, reszta przez loopback.</summary>
    private async Task<LinkContent> HandleStreamingAsync(ILinkPeer peer, RemoteSession session, IncomingTransfer request, CancellationToken ct)
    {
        var envelope = LinkWire.ReadRequest(request.Metadata);

        if (LinkFileRoutes.IsUpload(envelope))
            return await _files.ReceiveUploadAsync(peer, session, envelope, request, ct).ConfigureAwait(false);
        if (LinkFileRoutes.IsDownload(envelope))
            return _files.SendDownload(peer, session, envelope);

        // Reszta (exec, fs/list, read, write, ...) - małe ciała, przez loopback jak dotąd.
        var body = await request.ReadAllBytesAsync(ct).ConfigureAwait(false);
        var response = await ForwardAsync(envelope, body, session.Token, ct).ConfigureAwait(false);
        return LinkWire.Response(response);
    }

    private static LinkContent Answer(bool legacy, LinkResponse response) =>
        legacy ? LinkWire.LegacyResponse(response) : LinkWire.Response(response);

    private async Task<LinkResponse> ForwardAsync(LinkRequest call, ReadOnlyMemory<byte> body, string token, CancellationToken ct)
    {
        var path = call.Path.StartsWith('/') ? call.Path : "/" + call.Path;
        var url = $"http://127.0.0.1:{LoopbackPort}{path}";
        if (!string.IsNullOrEmpty(call.Query)) url += "?" + call.Query;

        using var message = new HttpRequestMessage(new HttpMethod(call.Method), url);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrWhiteSpace(call.Note))
            message.Headers.TryAddWithoutValidation("X-AVH-Note", call.Note);

        if (!body.IsEmpty)
        {
            message.Content = new ByteArrayContent(body.ToArray());
            // Media type z parametrami ("application/json; charset=utf-8") wywala ctor - parsujemy tolerancyjnie.
            // Brak typu to JSON: tak wysyła model do exec/write, a upload i tak omija tę ścieżkę.
            message.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(call.ContentType, out var mt)
                ? mt
                : new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
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

    internal static string Name(ILinkPeer peer) =>
        string.IsNullOrWhiteSpace(peer.Name) ? "unknown machine" : peer.Name;
}
