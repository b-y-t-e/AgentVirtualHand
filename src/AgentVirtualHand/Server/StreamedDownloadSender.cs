using Tailcat.Link;

namespace AgentVirtualHand.Server;

/// <summary>Pobrania zamawiane przez hub: plik idzie do huba transferem strumieniowym, nie w kopercie 16 MiB.</summary>
internal sealed class StreamedDownloadSender(ITransferGate gate)
{
    public static bool IsBeginRequest(LinkRequest call) =>
        call is { Method: "GET", Path: "/api/fs/download" } && LinkQuery.Value(call.Query, "transferId") is not null;

    /// <summary>Otwiera plik i zaczyna go pchac w tle; odpowiedz niesie tylko dlugosc i nazwe na naglowki.</summary>
    public LinkResponse Begin(ILinkPeer peer, RemoteSession session, LinkRequest call)
    {
        var transferId = LinkQuery.Value(call.Query, "transferId") ?? "";
        var path = LinkQuery.Value(call.Query, "path");
        if (string.IsNullOrWhiteSpace(path))
            return LinkResponse.Error(400, "Query parameter 'path' is required.");

        var full = Path.GetFullPath(path);
        gate.AuditStart(peer, call.Note, $"download {full}");
        if (!File.Exists(full))
            return LinkResponse.Error(404, "File does not exist: " + full);

        var info = new FileInfo(full);
        // FileStream jest przewijalny, wiec transfer wznowi sie po zerwaniu sesji - strumien odpowiedzi
        // HTTP z loopbacku by sie nie cofnal i padlby z "content cannot be rewound".
        // Wysylka w tle: peer.SendAsync konczy sie dopiero, gdy hub odbierze caly transfer, a hub czeka
        // najpierw na te odpowiedz, zeby ustawic naglowki - blokada tutaj zakleszczylaby obie strony.
        _ = PumpAsync(peer, session, File.OpenRead(full), transferId, info.Length, info.Name);

        return LinkResponse.Json(200, new { length = info.Length, name = info.Name });
    }

    private async Task PumpAsync(ILinkPeer peer, RemoteSession session, Stream source, string transferId, long length, string name)
    {
        // Wysylka zyje dluzej niz zadanie, ktore ja zamowilo - odciecie dostepu musi ja zatrzymac samo.
        using var watch = gate.Watch(session, CancellationToken.None);
        try
        {
            await using (source)
            {
                var offer = new TransferOffer
                {
                    Name = name,
                    ContentType = "application/octet-stream",
                    Length = length,
                    Metadata = LinkCodec.Encode(new TransferMeta { Op = "download", Id = transferId }),
                };
                var progress = TransferProgressLog.Create($"download {name}", line => gate.Audit("fs", peer, line));
                await peer.SendAsync(source, offer, progress, watch.Token).ConfigureAwait(false);
            }
            gate.Audit("fs", peer, $"download {name} sent");
        }
        catch (OperationCanceledException) when (watch.SessionEnded)
        {
            gate.Audit("deny", peer, $"download {name} stopped - access closed");
        }
        catch (Exception ex)
        {
            gate.Audit("deny", peer, $"download transfer failed - {ex.Message}");
        }
    }
}
