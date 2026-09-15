using System.Text.Json;
using AgentVirtualHand.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Tailcat.Link;

namespace AgentVirtualHand.Hub.Services;

/// <summary>Upload modelu: cialo leci do hosta transferem strumieniowym, wynik odbieramy malym "upload-finish".</summary>
internal sealed class StreamedUploadForwarder(ConnectionAudit audit)
{
    public const string RoutePath = "/api/fs/upload";

    /// <summary>Tyle bajtow miesci sie w kopercie 16 MiB po zakodowaniu base64 (+1/3), z zapasem na reszte pol.</summary>
    private const long MaxEnvelopeUploadBytes = 11 * 1024 * 1024;

    private sealed record UploadRequest(string Id, string TargetPath, string? Query, string? Note);

    public async Task ForwardAsync(HttpContext context, ILink link)
    {
        var query = LinkHttp.RawQuery(context);
        var target = LinkQuery.Value(query, "path");
        if (string.IsNullOrWhiteSpace(target))
        {
            await LinkHttp.WriteErrorAsync(context, 400, "Field 'path' is required.");
            return;
        }

        // Limit 512 MiB Kestrela chroni koperte w pamieci; ten endpoint spooluje na dysk i wysyla transferem.
        LiftRequestBodyLimit(context);

        var upload = new UploadRequest(Guid.NewGuid().ToString("n"), target, query, LinkHttp.Note(context));
        audit.Note(upload.Note);
        audit.Write("fs", $"upload {target}");

        // Cialo requestu na zywo nie da sie przewinac - gdy sesja relayu mrugnie w polowie, transfer
        // nie wznowi sie i pada z "content cannot be rewound". Zrzucamy je najpierw do pliku, bo
        // FileStream jest przewijalny i transfer wstaje od miejsca przerwania.
        var spool = Path.Combine(Path.GetTempPath(), "avh-up-" + upload.Id + ".tmp");
        try
        {
            await using (var file = File.Create(spool))
                await context.Request.Body.CopyToAsync(file, context.RequestAborted);

            await LinkHttp.WriteAsync(context, await SendAsync(link, upload, spool, context.RequestAborted));
        }
        catch (Exception ex)
        {
            if (!context.Response.HasStarted) await LinkHttp.WriteErrorAsync(context, 502, ex.Message);
        }
        finally
        {
            try { File.Delete(spool); } catch { /* plik w temp - system sprzatnie */ }
        }
    }

    /// <summary>
    /// Transfer strumieniowy z odbiorem wyniku przez "upload-finish". Gdy transfer nie przejdzie, plik mieszczacy
    /// sie w kopercie idzie jeszcze zwyklym zadaniem (np. starszy host bez obslugi transferow), a wiekszy
    /// zwraca powod podany przez hosta.
    /// </summary>
    private async Task<LinkResponse> SendAsync(ILink link, UploadRequest upload, string spool, CancellationToken ct)
    {
        var length = new FileInfo(spool).Length;
        try
        {
            await SendTransferAsync(link, upload, spool, length, ct);
        }
        catch (Exception transferError) when (!ct.IsCancellationRequested)
        {
            return length <= MaxEnvelopeUploadBytes
                ? await SendEnvelopeAsync(link, upload, spool, ct)
                : await HostVerdictAsync(link, upload, transferError, ct);
        }
        return await FinishAsync(link, upload, ct);
    }

    private async Task SendTransferAsync(ILink link, UploadRequest upload, string spool, long length, CancellationToken ct)
    {
        var offer = new TransferOffer
        {
            Name = Path.GetFileName(upload.TargetPath),
            ContentType = "application/octet-stream",
            Length = length,
            Metadata = LinkCodec.Encode(new TransferMeta { Op = "upload", Path = upload.TargetPath, Id = upload.Id, Note = upload.Note }),
        };
        await using var source = File.OpenRead(spool);
        await link.SendAsync(source, offer, audit.TransferProgress($"upload {upload.TargetPath}"), ct);
    }

    private static async Task<LinkResponse> SendEnvelopeAsync(ILink link, UploadRequest upload, string spool, CancellationToken ct)
    {
        var envelope = new LinkRequest
        {
            Method = "POST",
            Path = RoutePath,
            Query = upload.Query,
            BodyBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(spool, ct)),
            ContentType = "application/octet-stream",
            Note = upload.Note,
        };
        return await LinkHttp.RequestAsync(link, envelope, ct);
    }

    /// <summary>
    /// Transfer padl, ale host mogl go odebrac i odrzucic (np. brak prawa zapisu) - jego powod jest trwaly, a sam
    /// blad transportu dalby 502, po ktorym model ponawialby upload bez konca.
    /// </summary>
    private static async Task<LinkResponse> HostVerdictAsync(ILink link, UploadRequest upload, Exception transferError, CancellationToken ct)
    {
        var finish = await FinishAsync(link, upload, ct);
        return HostDecided(finish) ? finish : throw new IOException(transferError.Message, transferError);
    }

    /// <summary>504 - transfer nie dotarl do hosta; 404 - starszy host bez "upload-finish". W obu brak werdyktu.</summary>
    private static bool HostDecided(LinkResponse finish) =>
        finish.Status is not (StatusCodes.Status404NotFound or StatusCodes.Status504GatewayTimeout);

    private static Task<LinkResponse> FinishAsync(ILink link, UploadRequest upload, CancellationToken ct)
    {
        var finish = new LinkRequest
        {
            Method = "POST",
            Path = "/api/fs/upload-finish",
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(new { id = upload.Id, path = upload.TargetPath }),
        };
        return LinkHttp.RequestAsync(link, finish, ct);
    }

    private static void LiftRequestBodyLimit(HttpContext context)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = null;
    }
}
