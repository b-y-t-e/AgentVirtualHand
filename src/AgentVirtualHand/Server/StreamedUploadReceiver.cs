using System.Text.Json;
using Tailcat.Link;

namespace AgentVirtualHand.Server;

/// <summary>
/// Odbiera uploady wyslane z huba transferem strumieniowym (ponad koperte 16 MiB) i oddaje ich wynik
/// malym zadaniem "upload-finish", dzieki czemu ksztalt odpowiedzi HTTP zostaje ten sam.
/// </summary>
internal sealed class StreamedUploadReceiver(ITransferGate gate)
{
    /// <summary>
    /// Tyle "upload-finish" czeka na zapis pliku; brak wyniku to blad, nie ciche 200. Musi zmiescic sie
    /// w rundzie koperty, inaczej hub dostalby wyjatek transportu zamiast 504 z przyczyna.
    /// </summary>
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(ExecLimits.MaxResponseHoldSeconds);

    private readonly UploadOutcomeRegistry _outcomes = new();

    public static bool IsFinishRequest(LinkRequest call) => call is { Method: "POST", Path: "/api/fs/upload-finish" };

    /// <summary>Upload z huba: strumien laduje wprost na dysk, bez trzymania pliku w pamieci.</summary>
    public async Task ReceiveAsync(ILinkPeer peer, IncomingTransfer transfer, CancellationToken ct)
    {
        var meta = LinkCodec.Decode<TransferMeta>(transfer.Metadata);
        var outcome = _outcomes.For(meta.Id);
        try
        {
            var record = await SaveAsync(peer, meta, transfer, ct).ConfigureAwait(false);
            outcome.Succeed(record);
            gate.Audit("fs", peer, $"upload {record.Path} written");
        }
        catch (Exception ex)
        {
            outcome.Fail(ex);
            throw;
        }
    }

    public async Task<LinkResponse> FinishAsync(LinkRequest call, CancellationToken ct)
    {
        var id = UploadIdFrom(call.Body);
        if (string.IsNullOrEmpty(id))
            return LinkResponse.Error(400, "Field 'id' is required.");

        try
        {
            var record = await _outcomes.For(id).Result.WaitAsync(FinishTimeout, ct).ConfigureAwait(false);
            return LinkResponse.Json(200, new { path = record.Path, size = record.Size });
        }
        catch (TimeoutException)
        {
            return LinkResponse.Error(504, "The upload did not reach the machine - nothing was confirmed as written.");
        }
        catch (AccessClosedException ex)
        {
            return LinkResponse.Error(401, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return LinkResponse.Error(500, "Upload failed: " + ex.Message);
        }
        finally
        {
            _outcomes.Forget(id);
        }
    }

    private async Task<UploadRecord> SaveAsync(ILinkPeer peer, TransferMeta meta, IncomingTransfer transfer, CancellationToken ct)
    {
        if (gate.SessionFor(peer) is not { } session)
        {
            gate.Audit("deny", peer, "upload refused - access closed");
            throw new AccessClosedException();
        }

        if (meta.Op != "upload" || string.IsNullOrWhiteSpace(meta.Path))
            throw new InvalidOperationException("Upload transfer without a target path.");

        var full = Path.GetFullPath(meta.Path);
        gate.AuditStart(peer, meta.Note, $"upload {full}");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // Transfer pisze do pliku obok celu i podmienia go dopiero po odebraniu calosci - przerwany
        // upload nie moze zostawic istniejacego pliku nadpisanego polowa danych.
        var partial = PartialUploadPath(full);
        using var watch = gate.Watch(session, ct);
        try
        {
            await transfer.SaveToAsync(partial, null, watch.Token).ConfigureAwait(false);
            File.Move(partial, full, overwrite: true);
        }
        catch (OperationCanceledException) when (watch.SessionEnded)
        {
            gate.Audit("deny", peer, $"upload {full} stopped - access closed");
            throw new AccessClosedException();
        }
        finally
        {
            DeleteQuietly(partial);
        }
        return new UploadRecord(full, new FileInfo(full).Length);
    }

    /// <summary>Plik roboczy w tym samym katalogu, zeby podmiana byla atomowym przemianowaniem; nazwa neutralna.</summary>
    private static string PartialUploadPath(string target) =>
        Path.Combine(Path.GetDirectoryName(target)!, $"~{Guid.NewGuid():n}.tmp");

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); } catch (IOException) { /* nic do sprzatniecia albo plik zajety */ } catch (UnauthorizedAccessException) { }
    }

    private static string? UploadIdFrom(string? body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body ?? "{}");
            return doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
