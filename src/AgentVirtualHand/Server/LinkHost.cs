using System.Net.Http.Headers;
using System.Text;
using Tailcat.Link;

namespace AgentVirtualHand.Server;

/// <summary>
/// Wystawia maszynę przez Tailcat.Link zamiast bezpośredniego połączenia po IP.
/// Żądania z linku trafiają do lokalnego serwera HTTP na 127.0.0.1 - dzięki temu
/// cała logika API (exec, pliki, sesje) zostaje jedna, a link jest tylko transportem.
/// </summary>
public sealed class LinkHost : IAsyncDisposable
{
    private const string AppName = "agentvirtualhand";

    private readonly SessionManager _sessions;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private ILink? _link;

    public LinkHost(SessionManager sessions) => _sessions = sessions;

    public event Action<string, string>? Audit;
    public event Action? Changed;

    public bool IsHosting => _link is not null;
    public bool IsConnected => _link?.IsConnected ?? false;

    /// <summary>Kod, który wpisuje się raz po drugiej stronie: avh-link join &lt;kod&gt;.</summary>
    public string InvitationCode => _link?.InvitationCode.Value ?? "";

    public DateTimeOffset? InvitationExpiresAt => _link?.InvitationExpiresAt;

    /// <summary>Port lokalnego serwera HTTP, do którego przekazujemy żądania z linku.</summary>
    public int LoopbackPort { get; set; } = 8787;

    public async Task StartAsync(TimeSpan pairingWindow)
    {
        if (_link is not null) throw new InvalidOperationException("Link już działa.");

        var options = new LinkOptions
        {
            PairingWindow = pairingWindow,
            Log = message => Audit?.Invoke("link", message),
        };

        var link = await TailcatLink.HostAsync(AppName, options).ConfigureAwait(false);
        link.OnRequest(HandleAsync);
        link.Connected += () =>
        {
            Audit?.Invoke("link", "Druga maszyna połączona");
            Changed?.Invoke();
        };
        link.Disconnected += reason =>
        {
            Audit?.Invoke("link", $"Rozłączono: {reason}");
            Changed?.Invoke();
        };

        _link = link;
        Audit?.Invoke("link", $"Link gotowy, kod zaproszenia ważny do {link.InvitationExpiresAt?.LocalDateTime:HH:mm:ss}");
        Changed?.Invoke();
    }

    /// <summary>Nowy kod zaproszenia - stary przestaje działać.</summary>
    public async Task<string> RenewInvitationAsync()
    {
        if (_link is null) throw new InvalidOperationException("Link nie działa.");

        var code = await _link.RenewInvitationAsync().ConfigureAwait(false);
        Audit?.Invoke("link", "Wygenerowano nowy kod zaproszenia");
        Changed?.Invoke();
        return code.Value;
    }

    public async Task StopAsync()
    {
        if (_link is null) return;

        var link = _link;
        _link = null;
        await link.DisposeAsync().ConfigureAwait(false);
        Audit?.Invoke("link", "Link zatrzymany");
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _http.Dispose();
    }

    /// <summary>
    /// Sparowanie na poziomie Tailcata potwierdza tożsamość maszyny, ale nie wystarcza:
    /// dostęp musi być otwarty w oknie aplikacji i wygasa razem z sesją.
    /// </summary>
    private async Task<ReadOnlyMemory<byte>> HandleAsync(ReadOnlyMemory<byte> request, CancellationToken ct)
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

        var session = _sessions.Session;
        if (session is null)
            return LinkCodec.Encode(LinkResponse.Error(401,
                "Dostęp zamknięty - otwórz go w oknie AgentVirtualHand na maszynie zdalnej."));

        try
        {
            var response = await ForwardAsync(call, session.Token, ct).ConfigureAwait(false);
            return LinkCodec.Encode(response);
        }
        catch (Exception ex)
        {
            Audit?.Invoke("deny", $"Błąd obsługi żądania z linku: {ex.Message}");
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
}
