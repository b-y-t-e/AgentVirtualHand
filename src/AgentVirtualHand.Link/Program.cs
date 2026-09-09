using System.Text;
using System.Text.Json;
using AgentVirtualHand.Server;
using Tailcat.Link;
using Tailcat.Link.Storage;

namespace AgentVirtualHand.Link;

/// <summary>
/// Klient AgentVirtualHand. Łączy się przez Tailcat.Link - bez adresu IP, portu i firewalla.
/// Kod zaproszenia podaje się raz (join), potem sparowanie jest pamiętane.
/// </summary>
public static class Program
{
    private const string AppName = "agentvirtualhand";

    public static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { /* przekierowane wyjście */ }

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return args[0] switch
            {
                "--daemon" => await DaemonAsync(args),
                "up" => await UpAsync(args),
                "down" => await DownAsync(args),
                "join" => await JoinAsync(Arg(args, 1) ?? throw new ArgumentException("Podaj kod zaproszenia: avh-link join <kod>"), args),
                "forget" => await ForgetAsync(args),
                _ => await CallAsync(args),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>Pierwsze połączenie - jedyny moment, w którym kod zaproszenia jest potrzebny.</summary>
    private static async Task<int> JoinAsync(string code, string[] args)
    {
        await using var link = await TailcatLink.JoinAsync(AppName, code, Options(args));
        await link.WaitUntilConnectedAsync(Deadline(TimeSpan.FromSeconds(60)));

        Console.WriteLine("Sparowano. Kod nie będzie już potrzebny - spróbuj: avh-link system");
        return 0;
    }

    private static async Task<int> ForgetAsync(string[] args)
    {
        await TailcatLink.ForgetAsync(AppName, Options(args));
        Console.WriteLine("Sparowanie usunięte. Kolejne połączenie wymaga nowego kodu zaproszenia.");
        return 0;
    }

    private static async Task<int> CallAsync(string[] args)
    {
        var (request, saveTo) = Build(args);
        var response = await LinkDaemon.SendAsync(request, Store(args), TimeSpan.FromMinutes(10));

        return await WriteAsync(response, saveTo);
    }

    /// <summary>Proces tła trzymający otwarty link. Uruchamiany sam, nie przez użytkownika.</summary>
    private static async Task<int> DaemonAsync(string[] args)
    {
        var pipe = Arg(args, 1) ?? LinkDaemon.PipeName(Store(args));
        await LinkDaemon.RunAsync(pipe, Options(args));
        return 0;
    }

    private static async Task<int> UpAsync(string[] args)
    {
        var response = await LinkDaemon.SendAsync(new LinkRequest { Path = "/api/session" }, Store(args), TimeSpan.FromMinutes(2));
        Console.WriteLine(response.IsSuccess
            ? "Link gotowy. Kolejne polecenia idą już bez zestawiania połączenia."
            : $"Link gotowy, ale dostęp zamknięty: HTTP {response.Status} {response.Body}");
        return response.IsSuccess ? 0 : 1;
    }

    private static async Task<int> DownAsync(string[] args)
    {
        var stopped = await LinkDaemon.StopAsync(Store(args));
        Console.WriteLine(stopped ? "Link w tle zatrzymany." : "Link w tle nie działał.");
        return 0;
    }

    /// <summary>Zamienia polecenie z wiersza poleceń na kopertę protokołu.</summary>
    private static (LinkRequest Request, string? SaveTo) Build(string[] args)
    {
        var verb = args[0];
        var rest = args.Skip(1).ToArray();

        switch (verb)
        {
            case "system":
                return (Get("/api/system"), null);

            case "api":
                return (Get("/api/help"), null);

            case "session":
                return Arg(rest, 0) == "end"
                    ? (Post("/api/session/end", null), null)
                    : (Get("/api/session"), null);

            case "exec":
            {
                var command = Arg(rest, 0) ?? throw new ArgumentException("Podaj polecenie: avh-link exec \"<polecenie>\"");
                return (Post("/api/exec", Json(new
                {
                    command,
                    cwd = Option(rest, "--cwd"),
                    timeoutSeconds = OptionInt(rest, "--timeout") ?? 120,
                })), null);
            }

            case "bg":
                return (BackgroundRequest(rest), null);

            case "fs":
                return FileRequest(rest);

            default:
                throw new ArgumentException($"Nieznane polecenie: {verb}. Zobacz: avh-link --help");
        }
    }

    private static LinkRequest BackgroundRequest(string[] args)
    {
        var action = Arg(args, 0) ?? throw new ArgumentException("Użycie: avh-link bg start|out|stdin|kill ...");
        var rest = args.Skip(1).ToArray();

        switch (action)
        {
            case "start":
            {
                var command = Arg(rest, 0) ?? throw new ArgumentException("Podaj polecenie: avh-link bg start \"<polecenie>\"");
                return Post("/api/exec/start", Json(new
                {
                    command,
                    cwd = Option(rest, "--cwd"),
                    timeoutSeconds = OptionInt(rest, "--timeout") ?? 3600,
                }));
            }

            case "out":
            {
                var id = Required(rest, 0, "avh-link bg out <id>");
                var query = $"outOffset={OptionInt(rest, "--out-offset") ?? 0}&errOffset={OptionInt(rest, "--err-offset") ?? 0}";
                return Get($"/api/exec/{id}", query);
            }

            case "stdin":
            {
                var id = Required(rest, 0, "avh-link bg stdin <id> \"<tekst>\"");
                var text = Arg(rest, 1) ?? throw new ArgumentException("Podaj tekst do wysłania na stdin.");
                return Post($"/api/exec/{id}/stdin", text, "text/plain");
            }

            case "kill":
                return Post($"/api/exec/{Required(rest, 0, "avh-link bg kill <id>")}/kill", null);

            default:
                throw new ArgumentException($"Nieznana operacja tła: {action}");
        }
    }

    private static (LinkRequest Request, string? SaveTo) FileRequest(string[] args)
    {
        var action = Arg(args, 0) ?? throw new ArgumentException("Użycie: avh-link fs list|read|write|download|upload|mkdir|delete|move ...");
        var rest = args.Skip(1).ToArray();

        switch (action)
        {
            case "list":
                return (Get("/api/fs/list", $"path={Uri.EscapeDataString(Required(rest, 0, "avh-link fs list <ścieżka>"))}"), null);

            case "read":
            {
                var query = $"path={Uri.EscapeDataString(Required(rest, 0, "avh-link fs read <ścieżka>"))}";
                if (OptionInt(rest, "--max-bytes") is { } max) query += $"&maxBytes={max}";
                return (Get("/api/fs/read", query), null);
            }

            case "download":
            {
                var remote = Required(rest, 0, "avh-link fs download <zdalna> <lokalna>");
                var local = Required(rest, 1, "avh-link fs download <zdalna> <lokalna>");
                return (Get("/api/fs/download", $"path={Uri.EscapeDataString(remote)}"), local);
            }

            case "write":
            {
                var path = Required(rest, 0, "avh-link fs write <ścieżka> --text \"<treść>\"");
                var text = Option(rest, "--text")
                    ?? throw new ArgumentException("Podaj treść przez --text \"<treść>\" albo użyj fs upload.");
                return (Post("/api/fs/write", Json(new
                {
                    path,
                    content = text,
                    append = rest.Contains("--append"),
                })), null);
            }

            case "upload":
            {
                var local = Required(rest, 0, "avh-link fs upload <lokalna> <zdalna>");
                var remote = Required(rest, 1, "avh-link fs upload <lokalna> <zdalna>");
                var bytes = File.ReadAllBytes(local);
                return (new LinkRequest
                {
                    Method = "POST",
                    Path = "/api/fs/upload",
                    Query = $"path={Uri.EscapeDataString(remote)}",
                    BodyBase64 = Convert.ToBase64String(bytes),
                    ContentType = "application/octet-stream",
                }, null);
            }

            case "mkdir":
                return (Post("/api/fs/mkdir", Json(new { path = Required(rest, 0, "avh-link fs mkdir <ścieżka>") })), null);

            case "delete":
                return (Post("/api/fs/delete", Json(new
                {
                    path = Required(rest, 0, "avh-link fs delete <ścieżka>"),
                    recursive = rest.Contains("--recursive"),
                })), null);

            case "move":
                return (Post("/api/fs/move", Json(new
                {
                    from = Required(rest, 0, "avh-link fs move <z> <do>"),
                    to = Required(rest, 1, "avh-link fs move <z> <do>"),
                })), null);

            default:
                throw new ArgumentException($"Nieznana operacja na plikach: {action}");
        }
    }

    private static async Task<int> WriteAsync(LinkResponse response, string? saveTo)
    {
        if (saveTo is not null && response.IsSuccess)
        {
            var bytes = response.BodyBase64 is { } encoded
                ? Convert.FromBase64String(encoded)
                : Encoding.UTF8.GetBytes(response.Body ?? "");

            await File.WriteAllBytesAsync(saveTo, bytes);
            Console.WriteLine($"{saveTo}  ({bytes.Length} B)");
            return 0;
        }

        var text = response.Body ?? (response.BodyBase64 is { } raw
            ? $"[{Convert.FromBase64String(raw).Length} bajtów binarnych - użyj fs download]"
            : "");

        if (response.IsSuccess)
        {
            Console.WriteLine(text);
            return 0;
        }

        Console.Error.WriteLine($"HTTP {response.Status}: {text}");
        return 1;
    }

    /// <summary>
    /// Tożsamość klienta trzymana jest domyślnie tam, gdzie chce biblioteka.
    /// --store przydaje się, gdy na jednej maszynie działa i host, i klient - inaczej
    /// obie strony pisałyby po tym samym pliku sparowania.
    /// </summary>
    private static LinkOptions Options(string[] args)
    {
        var root = Store(args);
        return root is null
            ? new LinkOptions()
            : new LinkOptions { Store = new FileLinkStore(root, SecretProtector.ForCurrentPlatform()) };
    }

    private static string? Store(string[] args) =>
        Option(args, "--store") ?? Environment.GetEnvironmentVariable("AVH_LINK_STORE");

    private static LinkRequest Get(string path, string? query = null) =>
        new() { Method = "GET", Path = path, Query = query };

    private static LinkRequest Post(string path, string? body, string contentType = "application/json") =>
        new() { Method = "POST", Path = path, Body = body, ContentType = contentType };

    private static string Json<T>(T value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static CancellationToken Deadline(TimeSpan limit) => new CancellationTokenSource(limit).Token;

    private static string? Arg(string[] args, int index) =>
        index < args.Length && !args[index].StartsWith("--") ? args[index] : null;

    private static string Required(string[] args, int index, string usage) =>
        Arg(args, index) ?? throw new ArgumentException($"Użycie: {usage}");

    private static string? Option(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static int? OptionInt(string[] args, string name) =>
        int.TryParse(Option(args, name), out var value) ? value : null;

    private static void PrintUsage() => Console.WriteLine("""
        avh-link - zdalna powłoka i pliki przez Tailcat.Link (bez adresów IP i portów)

        POŁĄCZENIE
          avh-link join <kod>                 sparowanie, kod jednorazowy z okna AgentVirtualHand
          avh-link up                         zestawia link w tle (pierwsze polecenie robi to samo)
          avh-link down                       zamyka link w tle
          avh-link forget                     usuwa sparowanie z tej maszyny

        POLECENIA
          avh-link exec "<polecenie>" [--cwd <ścieżka>] [--timeout <sekundy>]
          avh-link bg start "<polecenie>" [--cwd <ścieżka>] [--timeout <sekundy>]
          avh-link bg out <id> [--out-offset N] [--err-offset N]
          avh-link bg stdin <id> "<tekst>"
          avh-link bg kill <id>

        PLIKI
          avh-link fs list <ścieżka>
          avh-link fs read <ścieżka> [--max-bytes N]
          avh-link fs download <zdalna> <lokalna>
          avh-link fs write <ścieżka> --text "<treść>" [--append]
          avh-link fs upload <lokalna> <zdalna>
          avh-link fs mkdir <ścieżka>
          avh-link fs delete <ścieżka> [--recursive]
          avh-link fs move <z> <do>

        RESZTA
          avh-link system                     system, powłoka, dyski, katalog domowy
          avh-link session                    pozostały czas dostępu
          avh-link session end                koniec pracy, zamyka dostęp
          avh-link api                        dokumentacja API maszyny zdalnej

        DODATKOWO
          --store <katalog>                   inne miejsce na sparowanie (albo AVH_LINK_STORE)
        """);
}
