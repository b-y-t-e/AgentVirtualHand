using System.Text;
using System.Text.Json;

namespace AgentVirtualHand.Server;

/// <summary>
/// Koperta wymieniana przez Tailcat.Link. Kod współdzielony z klientem avh-link,
/// dlatego trzyma się tu tylko struktury i serializacji - żadnej logiki którejś ze stron.
/// </summary>
public sealed record LinkRequest
{
    public string Method { get; init; } = "GET";

    /// <summary>Ścieżka API bez hosta, np. "/api/exec".</summary>
    public string Path { get; init; } = "/";

    /// <summary>Query string bez znaku zapytania, np. "path=C:/tmp&amp;maxBytes=2000".</summary>
    public string? Query { get; init; }

    public string? Body { get; init; }

    /// <summary>Treść binarna (upload pliku). Wyklucza się z <see cref="Body"/>.</summary>
    public string? BodyBase64 { get; init; }

    public string? ContentType { get; init; }

    /// <summary>Jedno zdanie intencji po ludzku - trafia do logu obok komendy.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Metadane transferu strumieniowego (SendAsync/OnTransfer). Jadą w polu <c>TransferOffer.Metadata</c>
/// jako bajty JSON i korelują transfer z żądaniem koordynującym. Współdzielone host&lt;-&gt;hub.
/// </summary>
public sealed record TransferMeta
{
    /// <summary>"upload" (hub -&gt; host) albo "download" (host -&gt; hub).</summary>
    public string Op { get; init; } = "";

    /// <summary>Ścieżka docelowa przy uploadzie; przy downloadzie zbędna (host zna ją z żądania).</summary>
    public string? Path { get; init; }

    /// <summary>Identyfikator korelacji - łączy transfer z żądaniem, które go zamówiło.</summary>
    public string Id { get; init; } = "";

    /// <summary>X-AVH-Note od modelu przy uploadzie - transfer omija loopback, wiec host loguje ja sam.</summary>
    public string? Note { get; init; }
}

public sealed record LinkResponse
{
    public int Status { get; init; } = 200;
    public string? ContentType { get; init; }
    public string? Body { get; init; }

    /// <summary>Treść binarna (download pliku).</summary>
    public string? BodyBase64 { get; init; }

    public bool IsSuccess => Status is >= 200 and < 300;

    public static LinkResponse Error(int status, string message) =>
        new() { Status = status, ContentType = "application/json", Body = $"{{\"error\":\"{Escape(message)}\"}}" };

    public static LinkResponse Json(int status, object body) =>
        new() { Status = status, ContentType = "application/json", Body = JsonSerializer.Serialize(body) };

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
}

public static class LinkCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] Encode<T>(T value) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Options));

    public static T Decode<T>(ReadOnlyMemory<byte> bytes) =>
        JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(bytes.Span), Options)
            ?? throw new InvalidOperationException("Pusta koperta w kanale linku.");
}
