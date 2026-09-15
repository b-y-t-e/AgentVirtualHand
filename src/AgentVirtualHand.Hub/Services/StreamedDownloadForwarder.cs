using System.Collections.Concurrent;
using System.Text.Json;
using AgentVirtualHand.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Tailcat.Link;

namespace AgentVirtualHand.Hub.Services;

/// <summary>Download modelu: prosimy host o pchniecie pliku, a nadchodzacy transfer przepinamy w odpowiedz HTTP.</summary>
internal sealed class StreamedDownloadForwarder(ConnectionAudit audit)
{
    public const string RoutePath = "/api/fs/download";

    /// <summary>Tyle czekamy, az host zacznie pchac zamowiony plik; potem 502 zamiast wiszacego curl.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Pobrania w toku, po korelacyjnym id - transfer wpadajacy przez OnTransfer musi trafic do wlasciwej odpowiedzi HTTP.</summary>
    private readonly ConcurrentDictionary<string, PendingDownload> _pending = new();

    private sealed class PendingDownload
    {
        public required HttpResponse Response { get; init; }
        // Host wysyla plik w tle i porazke tylko loguje - bez tego sygnalu nie wiemy, czy transfer w ogole ruszy.
        public TaskCompletionSource TransferStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Naglowki najpierw: transfer nie moze zaczac pisac ciala, zanim status i naglowki wyjda.
        public TaskCompletionSource HeadersReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PipeDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Co host podal o pliku w odpowiedzi na zamowienie - trafia do naglowkow.</summary>
    private sealed record FileDescription(long? Length, string Name)
    {
        public static FileDescription Parse(string? json)
        {
            long? length = null;
            var name = "download";
            try
            {
                using var doc = JsonDocument.Parse(json ?? "{}");
                if (doc.RootElement.TryGetProperty("length", out var l) && l.ValueKind == JsonValueKind.Number) length = l.GetInt64();
                if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString() ?? name;
            }
            catch (JsonException) { }
            return new FileDescription(length, name);
        }
    }

    public async Task ForwardAsync(HttpContext context, ILink link)
    {
        var id = Guid.NewGuid().ToString("n");
        var query = LinkHttp.RawQuery(context);
        var pending = new PendingDownload { Response = context.Response };
        _pending[id] = pending;

        var note = LinkHttp.Note(context);
        audit.Note(note);
        audit.Write("fs", $"download {LinkQuery.Value(query, "path")}");

        try
        {
            var begin = new LinkRequest
            {
                Method = "GET",
                Path = RoutePath,
                Query = string.IsNullOrEmpty(query) ? "transferId=" + id : query + "&transferId=" + id,
                // Host loguje notatke sam - to zadanie nie przechodzi przez filtr na loopbacku.
                Note = note,
            };
            var response = await LinkHttp.RequestAsync(link, begin, context.RequestAborted);

            // Blad albo starszy host, ktory nie zna transferId i oddal plik w kopercie - transferu nie bedzie,
            // oddajemy dokladnie to, co przyszlo.
            if (response.Status != 200 || response.BodyBase64 is not null)
            {
                pending.HeadersReady.TrySetCanceled();
                await LinkHttp.WriteAsync(context, response);
                return;
            }

            // Naglowki ustawiamy dopiero, gdy transfer ruszyl - do tej chwili blad nadal moze byc zwyklym 502.
            await pending.TransferStarted.Task.WaitAsync(StartTimeout, context.RequestAborted);
            WriteFileHeaders(context.Response, FileDescription.Parse(response.Body));

            // Naglowki gotowe - transfer moze zaczac pisac cialo.
            pending.HeadersReady.TrySetResult();
            await pending.PipeDone.Task.WaitAsync(context.RequestAborted);
        }
        catch (Exception ex)
        {
            pending.HeadersReady.TrySetCanceled();
            if (!context.Response.HasStarted)
                await LinkHttp.WriteErrorAsync(context, 502, ex is TimeoutException ? "The machine did not start sending the file." : ex.Message);
            else
                // Status 200 juz wyszedl - zrywamy polaczenie, zeby klient nie wzial urwanego pliku za caly.
                context.Abort();
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Jeden handler na polaczenie: rozklada nadchodzace transfery po korelacyjnym id na czekajace pobrania.</summary>
    public async Task ReceiveAsync(IncomingTransfer transfer, CancellationToken ct)
    {
        var meta = LinkCodec.Decode<TransferMeta>(transfer.Metadata);
        if (!_pending.TryGetValue(meta.Id, out var pending))
        {
            // Nikt nie czeka (klient sie rozlaczyl) - odrzucamy bez czytania: wyjatek z handlera konczy
            // SendAsync nadawcy, zamiast pompowac caly plik przez relay do Stream.Null.
            throw new InvalidOperationException("Nobody is waiting for this download any more.");
        }

        pending.TransferStarted.TrySetResult();
        try
        {
            await pending.HeadersReady.Task.WaitAsync(ct);
            await transfer.CopyToAsync(pending.Response.Body, null, ct);
            pending.PipeDone.TrySetResult();
        }
        catch (Exception ex)
        {
            // Czekajace pobranie musi sie dowiedziec o porazce, inaczej odpowiedz HTTP wisi bez konca.
            pending.PipeDone.TrySetException(ex);
            throw;
        }
    }

    private static void WriteFileHeaders(HttpResponse response, FileDescription file)
    {
        response.StatusCode = 200;
        response.ContentType = "application/octet-stream";
        if (file.Length is { } length) response.ContentLength = length;
        response.Headers.ContentDisposition = AttachmentHeader(file.Name);
    }

    /// <summary>Content-Disposition z poprawnie zakodowana nazwa (filename*), takze dla znakow spoza ASCII i cudzyslowow.</summary>
    private static string AttachmentHeader(string fileName)
    {
        var header = new ContentDispositionHeaderValue("attachment");
        header.SetHttpFileName(fileName);
        return header.ToString();
    }
}
