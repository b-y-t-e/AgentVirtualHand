using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace AgentVirtualHand.Hub.Services;

/// <summary>Drobne przejscie HTTP modelu &lt;-&gt; link. Sam transport zada w LinkConnection.ForwardAsync.</summary>
internal static class LinkHttp
{
    /// <summary>Query string bez znaku zapytania, tak jak niesie go koperta.</summary>
    public static string? RawQuery(HttpContext context) =>
        context.Request.QueryString.HasValue ? context.Request.QueryString.Value![1..] : null;

    public static string? Note(HttpContext context) =>
        context.Request.Headers["X-AVH-Note"].ToString() is { } note && !string.IsNullOrWhiteSpace(note) ? note : null;

    /// <summary>Limit ciała Kestrela chroni bufor w pamięci; strumieniowy upload go nie potrzebuje.</summary>
    public static void LiftRequestBodyLimit(HttpContext context)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = null;
    }

    /// <summary>Ciało requestu w przewijalnym pliku tymczasowym, który znika sam przy zamknięciu.</summary>
    public static async Task<FileStream> SpoolBodyAsync(HttpContext context)
    {
        var path = Path.Combine(Path.GetTempPath(), $"~{Guid.NewGuid():n}.tmp");
        var spool = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            await context.Request.Body.CopyToAsync(spool, context.RequestAborted);
            spool.Position = 0;
            return spool;
        }
        catch
        {
            await spool.DisposeAsync();
            throw;
        }
    }

    /// <summary>Content-Disposition z poprawnie zakodowaną nazwą (filename*), także dla znaków spoza ASCII.</summary>
    public static void WriteAttachmentName(HttpResponse response, string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return;
        var header = new ContentDispositionHeaderValue("attachment");
        header.SetHttpFileName(fileName);
        response.Headers.ContentDisposition = header.ToString();
    }

    public static async Task WriteErrorAsync(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted) return;   // ciało już poszło - nie da się nadpisać statusu
        context.Response.Clear();                  // nagłówki pliku (Content-Length, Content-Disposition) nie pasują do błędu
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = message });
    }
}
