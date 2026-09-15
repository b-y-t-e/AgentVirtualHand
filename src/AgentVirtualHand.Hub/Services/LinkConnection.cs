using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using AgentVirtualHand.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tailcat.Link;
using Tailcat.Link.Storage;

namespace AgentVirtualHand.Hub.Services;

/// <summary>
/// Jedno połączenie do jednej maszyny: link Tailcata plus własny serwer HTTP na 127.0.0.1.
/// Osobny port i osobny token na każdą maszynę to cała istota izolacji - prompt wygenerowany
/// dla jednego komputera nie daje dostępu do pozostałych.
/// </summary>
public sealed class LinkConnection : IAsyncDisposable
{
    private const string AppName = "agentvirtualhand";

    private ILink? _link;
    private WebApplication? _app;

    private readonly ConnectionAudit _audit;

    public LinkConnection(ConnectionEntry entry)
    {
        // Token zyje razem z wpisem, nie z procesem - inaczej restart aplikacji
        // unieważniałby każdy wcześniej skopiowany prompt.
        Entry = entry.Token.Length > 0 ? entry : entry with { Token = NewToken() };

        _audit = new ConnectionAudit(() => Entry.Name, (kind, text) => Audit?.Invoke(kind, text));
    }

    public ConnectionEntry Entry { get; private set; }

    /// <summary>Token wpuszczany przez lokalny serwer tego jednego połączenia.</summary>
    public string Token => Entry.Token;

    public int Port { get; private set; }

    public bool IsConnected => _link?.IsConnected ?? false;
    public bool IsOpen => _app is not null;

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public event Action<string, string>? Audit;
    public event Action? Changed;

    public void Rename(string name)
    {
        Entry = Entry with { Name = name };
        Changed?.Invoke();
    }

    /// <summary>
    /// Podnosi połączenie. Kod zaproszenia potrzebny jest wyłącznie za pierwszym razem -
    /// potem sparowanie leży w katalogu tego połączenia.
    /// </summary>
    public async Task StartAsync(string? invitationCode = null)
    {
        if (_app is not null) return;

        Directory.CreateDirectory(Entry.StoreRoot);

        var options = new LinkOptions
        {
            Store = new FileLinkStore(Entry.StoreRoot, SecretProtector.ForCurrentPlatform()),
            // Heartbeat czesciej niz 10-sekundowy prog ciszy - inaczej bezczynny link odpada co 10 s.
            HeartbeatInterval = TimeSpan.FromSeconds(5),
            Log = message => Audit?.Invoke("link", $"{Entry.Name}: {message}"),
        };

        // Nazwa trafia na liste maszyn po stronie hosta, wiec wlasciciel widzi, kto sie dobija.
        var request = new JoinRequest { DisplayName = $"{Environment.MachineName} (hub)" };

        var link = invitationCode is { Length: > 0 }
            ? await TailcatLink.JoinAsync(AppName, invitationCode, request, options).ConfigureAwait(false)
            : await TailcatLink.JoinAsync(AppName, options: options).ConfigureAwait(false);

        link.Connected += () =>
        {
            Audit?.Invoke("link", $"{Entry.Name}: connected");
            Changed?.Invoke();
        };
        link.Disconnected += reason =>
        {
            Audit?.Invoke("link", $"{Entry.Name}: disconnected ({reason})");
            Changed?.Invoke();
        };

        _link = link;

        // Przy pierwszym parowaniu trzeba poczekac na potwierdzenie: inaczej aplikacja
        // zameldowalaby "gotowe" dla polaczenia, ktore druga strona wlasnie odrzucila.
        if (invitationCode is { Length: > 0 })
        {
            using var pairing = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await link.WaitUntilConnectedAsync(pairing.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await link.DisposeAsync().ConfigureAwait(false);
                _link = null;
                throw new TimeoutException(
                    "The other machine did not confirm pairing. The code may have expired or the host is already paired with another machine - ask for a new code.");
            }
        }

        await StartLocalServerAsync().ConfigureAwait(false);

        Audit?.Invoke("link", $"{Entry.Name}: ready on {BaseUrl}");
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        var app = _app;
        var link = _link;
        _app = null;
        _link = null;

        if (app is not null)
        {
            await app.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }

        if (link is not null) await link.DisposeAsync().ConfigureAwait(false);

        if (app is not null || link is not null)
        {
            Audit?.Invoke("link", $"{Entry.Name}: turned off");
            Changed?.Invoke();
        }
    }

    /// <summary>Nowy token unieważnia wszystkie wcześniej wygenerowane prompty dla tej maszyny.</summary>
    public void RotateToken()
    {
        Entry = Entry with { Token = NewToken() };
        Audit?.Invoke("link", $"{Entry.Name}: new token, earlier prompts stopped working");
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task StartLocalServerAsync()
    {
        // Port z pliku, a przy zajętym - dowolny wolny. Kestrel binduje sam, więc nie ma
        // okna między sprawdzeniem a zajęciem portu.
        foreach (var port in new[] { Entry.Port, 0 })
        {
            if (port < 0) continue;

            try
            {
                var app = BuildLocalServer(port);
                await app.StartAsync().ConfigureAwait(false);

                _app = app;
                Port = ResolvePort(app, port);
                Entry = Entry with { Port = Port };
                return;
            }
            catch (Exception ex) when (port != 0)
            {
                Audit?.Invoke("link", $"{Entry.Name}: port {port} busy ({ex.Message}), taking a free one");
            }
        }
    }

    private WebApplication BuildLocalServer(int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(new IPEndPoint(IPAddress.Loopback, port));
            kestrel.Limits.MaxRequestBodySize = 512L * 1024 * 1024;
        });
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 512L * 1024 * 1024);

        var app = builder.Build();
        app.Map("/{**path}", ForwardAsync);
        return app;
    }

    private static int ResolvePort(WebApplication app, int requested)
    {
        if (requested != 0) return requested;

        var address = app.Urls.FirstOrDefault();
        return address is not null && Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Port : 0;
    }

    /// <summary>Każde żądanie z lokalnego portu idzie linkiem do tej jednej maszyny i do żadnej innej.</summary>
    private async Task ForwardAsync(HttpContext context)
    {
        if (!IsAuthorized(context))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or wrong token for this connection." });
            return;
        }

        var link = _link;
        if (link is null)
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsJsonAsync(new { error = "This connection is turned off." });
            return;
        }

        var envelope = new LinkRequest
        {
            Method = context.Request.Method,
            Path = context.Request.Path.Value ?? "/",
            Query = LinkHttp.RawQuery(context),
            ContentType = context.Request.ContentType,
            Note = LinkHttp.Note(context),
        };

        try
        {
            if (LinkFileRoutes.IsUpload(envelope))
                await ForwardUploadAsync(context, link, envelope);
            else
                await ForwardBufferedAsync(context, link, envelope);
        }
        catch (Exception) when (context.Response.HasStarted)
        {
            // Status i część ciała już wyszły - zrywamy połączenie, żeby klient nie wziął urwanego pliku za cały.
            context.Abort();
        }
        catch (Exception ex)
        {
            await LinkHttp.WriteErrorAsync(context, 502, ex.Message);
        }
    }

    /// <summary>
    /// Upload dowolnego rozmiaru. Ciała requestu na żywo nie da się przewinąć, więc gdy relay mrugnie
    /// w połowie, transfer nie wstałby od miejsca przerwania - najpierw zrzucamy je do pliku tymczasowego.
    /// </summary>
    private async Task ForwardUploadAsync(HttpContext context, ILink link, LinkRequest envelope)
    {
        // Brak ścieżki odrzucamy od razu - bez zrzucania całego ciała na dysk i przez relay po to samo 400.
        if (LinkQuery.FilePath(envelope.Query) is not { } path)
        {
            await LinkHttp.WriteErrorAsync(context, 400, LinkFileRoutes.MissingPathMessage);
            return;
        }

        _audit.Note(envelope.Note);
        _audit.Write("fs", $"upload {path}");

        LinkHttp.LiftRequestBodyLimit(context);
        await using var spool = await LinkHttp.SpoolBodyAsync(context);
        await SendAsync(context, link, LinkWire.Request(envelope, spool, spool.Length, leaveOpen: true));
    }

    /// <summary>Małe ciało (exec, list, write) buforujemy, żeby pokazać je w logu.</summary>
    private async Task ForwardBufferedAsync(HttpContext context, ILink link, LinkRequest envelope)
    {
        var body = await ReadBodyAsync(context);
        LogForwarded(envelope with { Body = Encoding.UTF8.GetString(body) });
        await SendAsync(context, link, LinkWire.Request(envelope, body));
    }

    /// <summary>Odpowiedź dowolnego rozmiaru (download) przepisujemy strumieniem 1:1 w odpowiedź HTTP.</summary>
    private static async Task SendAsync(HttpContext context, ILink link, LinkContent content)
    {
        await using var answer = await link.RequestAsync(content, context.RequestAborted);
        var response = LinkWire.ReadResponse(answer);
        context.Response.StatusCode = response.Status;
        if (response.ContentType is not null) context.Response.ContentType = response.ContentType;
        LinkHttp.WriteAttachmentName(context.Response, response.FileName);
        // Znana długość pozwala klientowi rozpoznać urwany plik.
        if (answer.Length is { } length) context.Response.ContentLength = length;
        await answer.CopyToAsync(context.Response.Body, null, context.RequestAborted);
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        return buffer.ToArray();
    }

    /// <summary>Pokazuje w logu, co i do ktorej maszyny wysylamy - jak w oknie hosta.</summary>
    private void LogForwarded(LinkRequest r)
    {
        var (kind, text) = Summarize(r);
        if (kind is null) return;

        _audit.Note(r.Note);
        _audit.Write(kind, text);
    }

    private static (string? Kind, string Text) Summarize(LinkRequest r)
    {
        var path = r.Path;
        if (path is "/api/exec" or "/api/exec/start")
            // Kolejnosc jak na hoscie: gdy przyszly oba pola, wykonuje sie skrypt, nie 'command'.
            return ("exec", ExecSummary(r.Body));

        if (path.StartsWith("/api/fs/", StringComparison.Ordinal))
        {
            var op = path["/api/fs/".Length..];
            if (op is "write" or "mkdir" or "delete" or "download")
                return ("fs", $"{op} {Field(r.Body, "path") ?? LinkQuery.Value(r.Query, "path")}");
            if (op is "move")
                return ("fs", $"move {Field(r.Body, "from")} -> {Field(r.Body, "to")}");
        }

        // Reszta (system, session, help, polling wyniku, stdin/kill) to szum - nie logujemy.
        return (null, "");
    }

    /// <summary>Skrypt (jak w logu hosta) albo 'command'; niepoprawne 'scriptBase64' host odrzuca, wiec tak je opisujemy.</summary>
    private static string ExecSummary(string? json)
    {
        var script = Field(json, "script");
        var scriptBase64 = Field(json, "scriptBase64");
        if (!ExecScript.IsPresent(script, scriptBase64)) return Field(json, "command") ?? "exec";

        return ExecScript.Decode(script, scriptBase64) is { } body
            ? "[script] " + body
            : "[script] rejected: scriptBase64 is not base64 text";
    }

    private static string? Field(string? json, string name)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch { return null; }
    }

    private bool IsAuthorized(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header[7..].Trim()
            : context.Request.Headers["X-AVH-Token"].ToString();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(Token));
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
        .Replace("+", "").Replace("/", "").Replace("=", "");
}
