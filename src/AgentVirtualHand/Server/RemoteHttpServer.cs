using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentVirtualHand.Server;



/// <summary>
/// Serwer HTTP wystawiający zdalne sterowanie maszyną.
/// Wszystko poza /api/pair oraz /api/status wymaga waznego tokenu sesji.
/// </summary>
public sealed class RemoteHttpServer : IAsyncDisposable
{
    /// <summary>UTF-8 bez BOM - pliki konfiguracyjne i skrypty nie znoszą znacznika BOM.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly SessionManager _sessions;
    private readonly ShellRunner _shell;
    private WebApplication? _app;

    public RemoteHttpServer(SessionManager sessions, ShellRunner shell)
    {
        _sessions = sessions;
        _shell = shell;
    }

    public bool IsRunning => _app is not null;

    /// <summary>Port przydzielony przez system - potrzebny tylko LinkHostowi do przekazywania zadan.</summary>
    public int Port { get; private set; }

    public event Action<string, string>? Audit;

    /// <summary>
    /// Wewnetrzna szyna HTTP na 127.0.0.1. Port wybiera system (port 0), bo z zewnatrz
    /// nikt sie tu nie laczy - jedyna droga prowadzi przez link.
    /// </summary>
    public async Task StartAsync()
    {
        if (_app is not null) throw new InvalidOperationException("Server already running.");

        var app = BuildApp(IPAddress.Loopback, 0);
        await app.StartAsync();

        _app = app;
        Port = ResolvePort(app);
        Audit?.Invoke("server", $"API ready on 127.0.0.1:{Port} (link access only)");
    }

    private static int ResolvePort(WebApplication app)
    {
        var address = app.Urls.FirstOrDefault();
        return address is not null && Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Port : 0;
    }

    private WebApplication BuildApp(IPAddress address, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        // Bez zewnetrznej konfiguracji. Kestrel DOKLADA endpointy z sekcji "Kestrel" w
        // konfiguracji (np. zmienna Kestrel__Endpoints__Http__Url ustawiona na maszynie)
        // do tego z kodu. Taki dodatkowy endpoint na zajetym porcie wywraca caly start
        // serwera, mimo ze nasz wlasny port jest wolny.
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Jawny IPEndPoint - Kestrel sam wlaczy DualMode dla IPv6Any i nie uruchomi
            // swojej wewnetrznej sciezki AnyIPListenOptions.
            kestrel.Listen(new IPEndPoint(address, port));
            kestrel.Limits.MaxRequestBodySize = 512L * 1024 * 1024; // pozwalamy wgrywać wieksze pliki
        });
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 512L * 1024 * 1024);

        var app = builder.Build();
        MapEndpoints(app);
        return app;
    }


    public async Task StopAsync()
    {
        if (_app is null) return;
        var app = _app;
        _app = null;
        _shell.KillAll();
        await app.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
        Audit?.Invoke("server", "Serwer zatrzymany");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/status", () => Results.Json(new
        {
            app = "AVH",
            version = AppInfo.Version,
            host = Environment.MachineName,
            os = AppInfo.OsDescription,
            openSessions = _sessions.Sessions.Count
        }, Json));

        var api = app.MapGroup("/api").AddEndpointFilter(AuthFilter);

        api.MapGet("/help", () => Results.Text(HelpText.Markdown(), "text/markdown; charset=utf-8"));

        api.MapGet("/session", (HttpContext ctx) =>
        {
            var session = Caller(ctx)!;
            return Results.Json(new
            {
                issuedAt = session.IssuedAt,
                expiresAt = session.ExpiresAt,
                remainingSeconds = (int)(_sessions.RemainingFor(session.PeerKey) ?? TimeSpan.Zero).TotalSeconds,
                clientName = session.ClientName,
                requests = session.RequestCount,
                otherMachinesConnected = _sessions.Sessions.Count - 1
            }, Json);
        });

        api.MapPost("/session/end", (HttpContext ctx) =>
        {
            // Konczy dostep tylko tego klienta - pozostale maszyny pracuja dalej.
            _sessions.Revoke(Caller(ctx)!.PeerKey, "ended by the client");
            return Results.Json(new { ended = true }, Json);
        });

        api.MapPost("/exec", async (HttpContext ctx) =>
        {
            var (request, jsonError) = await ReadJsonAsync<ExecRequest>(ctx);
            if (jsonError is not null) return jsonError;
            if (string.IsNullOrWhiteSpace(request?.Command))
                return Results.Json(new { error = "Pole 'command' jest wymagane." }, Json, statusCode: 400);

            Audit?.Invoke("exec", Trim(request.Command));
            var result = await _shell.RunAsync(
                request.Command,
                request.Shell,
                request.Cwd,
                request.TimeoutSeconds ?? 120,
                ctx.RequestAborted);

            return Results.Json(result, Json);
        });

        api.MapPost("/exec/start", async (HttpContext ctx) =>
        {
            var (request, jsonError) = await ReadJsonAsync<ExecRequest>(ctx);
            if (jsonError is not null) return jsonError;
            if (string.IsNullOrWhiteSpace(request?.Command))
                return Results.Json(new { error = "Pole 'command' jest wymagane." }, Json, statusCode: 400);

            Audit?.Invoke("exec", "[bg] " + Trim(request.Command));
            var managed = _shell.Start(request.Command, request.Shell, request.Cwd);
            return Results.Json(new { id = managed.Id, startedAt = managed.StartedAt, cwd = managed.WorkingDirectory }, Json);
        });

        api.MapGet("/exec/{id}", (string id, int? outOffset, int? errOffset) =>
        {
            var managed = _shell.Get(id);
            if (managed is null) return Results.Json(new { error = "Nie ma takiego procesu." }, Json, statusCode: 404);

            var (stdout, stderr, newOut, newErr) = managed.ReadFrom(outOffset ?? 0, errOffset ?? 0);
            return Results.Json(new
            {
                id = managed.Id,
                running = managed.ExitedAt is null,
                exitCode = managed.ExitCode,
                stdout,
                stderr,
                outOffset = newOut,
                errOffset = newErr
            }, Json);
        });

        api.MapPost("/exec/{id}/stdin", async (string id, HttpContext ctx) =>
        {
            var managed = _shell.Get(id);
            if (managed is null) return Results.Json(new { error = "Nie ma takiego procesu." }, Json, statusCode: 404);

            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            await managed.Process.StandardInput.WriteLineAsync(text);
            await managed.Process.StandardInput.FlushAsync();
            return Results.Json(new { sent = true }, Json);
        });

        api.MapPost("/exec/{id}/kill", (string id) =>
            _shell.Kill(id)
                ? Results.Json(new { killed = true }, Json)
                : Results.Json(new { error = "Nie ma takiego procesu." }, Json, statusCode: 404));

        api.MapGet("/fs/list", (string path) =>
        {
            var full = Path.GetFullPath(path);
            if (!Directory.Exists(full))
                return Results.Json(new { error = "Katalog nie istnieje: " + full }, Json, statusCode: 404);

            var dir = new DirectoryInfo(full);
            return Results.Json(new
            {
                path = dir.FullName,
                parent = dir.Parent?.FullName,
                directories = dir.EnumerateDirectories().Select(d => new { name = d.Name, modified = d.LastWriteTime }),
                files = dir.EnumerateFiles().Select(f => new { name = f.Name, size = f.Length, modified = f.LastWriteTime })
            }, Json);
        });

        api.MapGet("/fs/read", async (string path, int? maxBytes) =>
        {
            if (!File.Exists(path))
                return Results.Json(new { error = "Plik nie istnieje: " + path }, Json, statusCode: 404);

            var limit = Math.Clamp(maxBytes ?? 1_000_000, 1, 20_000_000);
            var info = new FileInfo(path);
            var buffer = new byte[Math.Min(limit, info.Length)];
            await using (var stream = File.OpenRead(path))
                _ = await stream.ReadAsync(buffer);

            return Results.Json(new
            {
                path = info.FullName,
                size = info.Length,
                truncated = info.Length > buffer.Length,
                content = Encoding.UTF8.GetString(buffer)
            }, Json);
        });

        api.MapGet("/fs/download", (string path) =>
            File.Exists(path)
                ? Results.File(Path.GetFullPath(path), "application/octet-stream", Path.GetFileName(path))
                : Results.Json(new { error = "Plik nie istnieje: " + path }, Json, statusCode: 404));

        api.MapPost("/fs/write", async (HttpContext ctx) =>
        {
            var (request, jsonError) = await ReadJsonAsync<WriteRequest>(ctx);
            if (jsonError is not null) return jsonError;
            if (string.IsNullOrWhiteSpace(request?.Path))
                return Results.Json(new { error = "Pole 'path' jest wymagane." }, Json, statusCode: 400);

            var full = Path.GetFullPath(request.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            if (request.ContentBase64 is not null)
                await File.WriteAllBytesAsync(full, Convert.FromBase64String(request.ContentBase64));
            else if (request.Append == true)
                await File.AppendAllTextAsync(full, request.Content ?? "", Utf8NoBom);
            else
                await File.WriteAllTextAsync(full, request.Content ?? "", Utf8NoBom);

            Audit?.Invoke("fs", "write " + full);
            return Results.Json(new { path = full, size = new FileInfo(full).Length }, Json);
        });

        api.MapPost("/fs/upload", async (string path, HttpContext ctx) =>
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await using (var file = File.Create(full))
                await ctx.Request.Body.CopyToAsync(file);

            Audit?.Invoke("fs", "upload " + full);
            return Results.Json(new { path = full, size = new FileInfo(full).Length }, Json);
        });

        api.MapPost("/fs/mkdir", async (HttpContext ctx) =>
        {
            var (request, jsonError) = await ReadJsonAsync<PathRequest>(ctx);
            if (jsonError is not null) return jsonError;
            if (string.IsNullOrWhiteSpace(request?.Path))
                return Results.Json(new { error = "Pole 'path' jest wymagane." }, Json, statusCode: 400);

            var dir = Directory.CreateDirectory(Path.GetFullPath(request.Path));
            return Results.Json(new { path = dir.FullName }, Json);
        });

        api.MapPost("/fs/delete", async (HttpContext ctx) =>
        {
            var (request, jsonError) = await ReadJsonAsync<PathRequest>(ctx);
            if (jsonError is not null) return jsonError;
            if (string.IsNullOrWhiteSpace(request?.Path))
                return Results.Json(new { error = "Pole 'path' jest wymagane." }, Json, statusCode: 400);

            var full = Path.GetFullPath(request.Path);
            if (Directory.Exists(full)) Directory.Delete(full, recursive: request.Recursive ?? false);
            else if (File.Exists(full)) File.Delete(full);
            else return Results.Json(new { error = "Path does not exist: " + full }, Json, statusCode: 404);

            Audit?.Invoke("fs", "delete " + full);
            return Results.Json(new { deleted = full }, Json);
        });

        api.MapPost("/fs/move", async (HttpContext ctx) =>
        {
            var (request, jsonError) = await ReadJsonAsync<MoveRequest>(ctx);
            if (jsonError is not null) return jsonError;
            if (string.IsNullOrWhiteSpace(request?.From) || string.IsNullOrWhiteSpace(request.To))
                return Results.Json(new { error = "Fields 'from' and 'to' are required." }, Json, statusCode: 400);

            var from = Path.GetFullPath(request.From);
            var to = Path.GetFullPath(request.To);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);

            if (Directory.Exists(from)) Directory.Move(from, to);
            else File.Move(from, to, overwrite: true);

            Audit?.Invoke("fs", $"move {from} -> {to}");
            return Results.Json(new { from, to }, Json);
        });

        api.MapGet("/system", () => Results.Json(new
        {
            host = Environment.MachineName,
            user = Environment.UserName,
            os = AppInfo.OsDescription,
            isWindows = ShellRunner.IsWindows,
            defaultShell = ShellRunner.DefaultShell,
            cwd = Environment.CurrentDirectory,
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            processors = Environment.ProcessorCount,
            uptimeSeconds = (long)(Environment.TickCount64 / 1000),
            drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new
            {
                name = d.Name,
                format = d.DriveFormat,
                totalGb = Math.Round(d.TotalSize / 1024d / 1024 / 1024, 1),
                freeGb = Math.Round(d.AvailableFreeSpace / 1024d / 1024 / 1024, 1)
            })
        }, Json));
    }

    private const string CallerKey = "avh.session";

    /// <summary>Sesja klienta, ktory wykonuje to zadanie - ustawiana przez filtr uwierzytelniajacy.</summary>
    private static RemoteSession? Caller(HttpContext ctx) => ctx.Items[CallerKey] as RemoteSession;

    private async ValueTask<object?> AuthFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header[7..].Trim()
            : http.Request.Headers["X-AVH-Token"].ToString();

        if (_sessions.Validate(token) is not { } session)
        {
            Audit?.Invoke("deny", $"401 {http.Request.Method} {http.Request.Path}");
            return Results.Json(new { error = "No valid access. Ask the machine owner to grant access." },
                Json, statusCode: StatusCodes.Status401Unauthorized);
        }

        http.Items[CallerKey] = session;

        // Intencja od agenta: czytelne zdanie, ktore ma sie pojawic w logu obok surowej komendy.
        var note = http.Request.Headers["X-AVH-Note"].ToString();
        if (!string.IsNullOrWhiteSpace(note)) Audit?.Invoke("note", Trim(note));

        return await next(ctx);
    }

    /// <summary>Czyta body jako JSON. Błąd parsowania zwracamy wprost - inaczej klient widzi mylące "brak pola".</summary>
    private static async Task<(T? Value, IResult? Error)> ReadJsonAsync<T>(HttpContext ctx)
    {
        try
        {
            var value = await ctx.Request.ReadFromJsonAsync<T>(Json);
            return value is null
                ? (default, Results.Json(new { error = "Puste body - oczekiwano obiektu JSON." }, Json, statusCode: 400))
                : (value, null);
        }
        catch (JsonException ex)
        {
            return (default, Results.Json(
                new { error = "Invalid JSON in body: " + ex.Message + " (remember to double backslashes in Windows paths)." },
                Json, statusCode: 400));
        }
    }

    private static string Trim(string text)
        => text.Length <= 160 ? text : text[..160] + "...";

    private sealed record PairRequest(string? Code, string? Client);
    private sealed record ExecRequest(string? Command, string? Shell, string? Cwd, int? TimeoutSeconds);
    private sealed record WriteRequest(string? Path, string? Content, string? ContentBase64, bool? Append);
    private sealed record PathRequest(string? Path, bool? Recursive);
    private sealed record MoveRequest(string? From, string? To);
}

public static class AppInfo
{
    public static string Version => "1.0.0";

    public static string OsDescription => System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim();

    public static string ProcessName => Process.GetCurrentProcess().ProcessName;
}
