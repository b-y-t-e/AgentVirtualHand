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
