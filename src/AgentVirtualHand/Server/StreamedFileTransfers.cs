using Tailcat.Link;

namespace AgentVirtualHand.Server;

/// <summary>
/// Upload i download plików strumieniem prosto na dysk / z dysku - nigdy w całości w pamięci.
/// Transfer omija loopback i jego filtr sesji, więc sam wiąże plik z sesją i sam loguje operacje.
/// </summary>
internal sealed class StreamedFileTransfers(SessionManager sessions, Action<string, string> audit, TimeProvider clock)
{
    private const int MaxNoteLength = 160;

    public async Task<LinkContent> ReceiveUploadAsync(ILinkPeer peer, RemoteSession session, LinkRequest envelope, IncomingTransfer request, CancellationToken ct)
    {
        if (LinkQuery.FilePath(envelope.Query) is not { } path)
            return MissingPath();

        var full = Path.GetFullPath(path);
        AuditOperation(peer, envelope, $"upload {full}");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        EnsureTargetWritable(full);

        // Transfer pisze do pliku obok celu i podmienia go dopiero po odebraniu całości - przerwany
        // upload nie może zostawić istniejącego pliku nadpisanego połową danych.
        var partial = PartialUploadPath(full);
        try
        {
            await using (var file = BindToSession(File.Create(partial), peer, session, $"upload {full}"))
                await request.CopyToAsync(file, null, ct).ConfigureAwait(false);
            File.Move(partial, full, overwrite: true);
        }
        catch (AccessClosedException ex)
        {
            return LinkWire.Response(LinkResponse.Error(401, ex.Message));
        }
        finally
        {
            DeleteQuietly(partial);
        }

        return LinkWire.Response(LinkResponse.Json(200, new { path = full, size = new FileInfo(full).Length }));
    }

    /// <summary>Zablokowany lub tylko do odczytu cel odmawia od razu, a nie po całym transferze. Bez obcinania.</summary>
    private static void EnsureTargetWritable(string target)
    {
        if (File.Exists(target))
            new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose();
    }

    /// <summary>Plik roboczy w tym samym katalogu, żeby podmiana była atomowym przemianowaniem; nazwa neutralna.</summary>
    private static string PartialUploadPath(string target) =>
        Path.Combine(Path.GetDirectoryName(target)!, $"~{Guid.NewGuid():n}.tmp");

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); } catch (IOException) { /* plik zajęty */ } catch (UnauthorizedAccessException) { }
    }

    public LinkContent SendDownload(ILinkPeer peer, RemoteSession session, LinkRequest envelope)
    {
        if (LinkQuery.FilePath(envelope.Query) is not { } path)
            return MissingPath();

        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            return LinkWire.Response(LinkResponse.Error(404, "File does not exist: " + full));

        AuditOperation(peer, envelope, $"download {full}");
        // Przewijalny strumień z dysku przeżywa reconnecty i pada, gdy właściciel odetnie dostęp.
        var file = BindToSession(File.OpenRead(full), peer, session, $"download {full}");
        var fileEnvelope = new LinkResponse { Status = 200, ContentType = "application/octet-stream", FileName = Path.GetFileName(full) };
        return LinkWire.Response(file, fileEnvelope);
    }

    private static LinkContent MissingPath() =>
        LinkWire.Response(LinkResponse.Error(400, LinkFileRoutes.MissingPathMessage));

    /// <summary>
    /// Transfer trwa tylko w obrębie sesji, w której ruszył: odcięcie i ponowne nadanie dostępu daje nowy token,
    /// więc stary transfer staje, zamiast ciągnąć się na nowym nadaniu. Download wysyła biblioteka już po
    /// zwróceniu odpowiedzi, więc wpis o zatrzymaniu robimy w chwili wykrycia, nie w catch wokół kopiowania.
    /// </summary>
    private SessionBoundStream BindToSession(Stream file, ILinkPeer peer, RemoteSession session, string operation)
    {
        var stopReported = false;
        return new SessionBoundStream(file, IsSessionOpen, clock);

        bool IsSessionOpen()
        {
            if (sessions.ForPeer(session.PeerKey)?.Token == session.Token) return true;
            if (!stopReported)
            {
                stopReported = true;
                audit("deny", $"{LinkHost.Name(peer)}: {operation} stopped - access closed");
            }
            return false;
        }
    }

    private void AuditOperation(ILinkPeer peer, LinkRequest envelope, string operation)
    {
        if (!string.IsNullOrWhiteSpace(envelope.Note))
            audit("note", $"{LinkHost.Name(peer)}: {Cap(envelope.Note)}");
        audit("fs", $"{LinkHost.Name(peer)}: {operation}");
    }

    private static string Cap(string text) =>
        text.Length <= MaxNoteLength ? text : text[..MaxNoteLength] + "...";
}
