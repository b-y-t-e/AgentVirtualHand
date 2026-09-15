using System.Text;
using AgentVirtualHand.Server;
using Microsoft.AspNetCore.Http;
using Tailcat.Link;

namespace AgentVirtualHand.Hub.Services;

/// <summary>Przejscie HTTP modelu &lt;-&gt; koperta linku, wspolne dla zwyklego forwardu i transferow plikow.</summary>
internal static class LinkHttp
{
    /// <summary>Query string bez znaku zapytania, tak jak niesie go koperta.</summary>
    public static string? RawQuery(HttpContext context) =>
        context.Request.QueryString.HasValue ? context.Request.QueryString.Value![1..] : null;

    public static string? Note(HttpContext context) =>
        context.Request.Headers["X-AVH-Note"].ToString() is { } note && !string.IsNullOrWhiteSpace(note) ? note : null;

    public static async Task<LinkResponse> RequestAsync(ILink link, LinkRequest request, CancellationToken ct) =>
        LinkCodec.Decode<LinkResponse>(await link.RequestAsync(LinkCodec.Encode(request), ct));

    /// <summary>Odpowiedz z koperty przepisana 1:1 w odpowiedz HTTP dla modelu.</summary>
    public static async Task WriteAsync(HttpContext context, LinkResponse response)
    {
        context.Response.StatusCode = response.Status;
        if (response.ContentType is not null) context.Response.ContentType = response.ContentType;

        if (response.BodyBase64 is { } encoded)
            await context.Response.Body.WriteAsync(Convert.FromBase64String(encoded));
        else if (response.Body is { } text)
            await context.Response.WriteAsync(text, Encoding.UTF8);
    }

    public static async Task WriteErrorAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = message });
    }
}
