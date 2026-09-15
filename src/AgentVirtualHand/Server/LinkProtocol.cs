using System.Text;
using System.Text.Json;
using Tailcat.Link;

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

public sealed record LinkResponse
{
    public int Status { get; init; } = 200;
    public string? ContentType { get; init; }
    public string? Body { get; init; }

    /// <summary>Treść binarna (download pliku).</summary>
    public string? BodyBase64 { get; init; }

    /// <summary>Nazwa pobieranego pliku - hub oddaje ją w Content-Disposition.</summary>
    public string? FileName { get; init; }

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

/// <summary>
/// Trasy plików, które hub i host prowadzą strumieniem zamiast buforem. Wspólne dla obu stron, żeby
/// żądanie nie trafiło po jednej stronie do strumienia, a po drugiej do pamięci.
/// </summary>
public static class LinkFileRoutes
{
    public const string Upload = "/api/fs/upload";
    public const string Download = "/api/fs/download";
    public const string MissingPathMessage = "Field 'path' is required.";

    public static bool IsUpload(LinkRequest request) => Matches(request, "POST", Upload);

    public static bool IsDownload(LinkRequest request) => Matches(request, "GET", Download);

    private static bool Matches(LinkRequest request, string method, string path) =>
        string.Equals(request.Method, method, StringComparison.OrdinalIgnoreCase)
        && string.Equals(request.Path, path, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Nowy transport (Tailcat.Link 0.5.0): koperta bez ciała jedzie w <c>LinkContent.Metadata</c>, a ciało
/// jest samą treścią - strumieniem dowolnego rozmiaru, nigdy w całości w pamięci. Zastępuje stary
/// most transferowy: <c>RequestAsync(LinkContent) -&gt; IncomingTransfer</c> niesie i kilobajt, i 20 GB.
/// </summary>
public static class LinkWire
{
    /// <summary>Koperta żądania (metoda/ścieżka/query/nota) bez ciała - to leci w metadanych.</summary>
    public static ReadOnlyMemory<byte> RequestMeta(LinkRequest request) =>
        LinkCodec.Encode(request with { Body = null, BodyBase64 = null });

    public static LinkRequest ReadRequest(ReadOnlyMemory<byte> metadata) =>
        LinkCodec.Decode<LinkRequest>(metadata);

    /// <summary>Koperta odpowiedzi (status/typ) bez ciała.</summary>
    public static ReadOnlyMemory<byte> ResponseMeta(LinkResponse response) =>
        LinkCodec.Encode(response with { Body = null, BodyBase64 = null });

    public static LinkResponse ReadResponse(IncomingTransfer answer) =>
        LinkCodec.Decode<LinkResponse>(answer.Metadata);

    /// <summary>Bajty ciała odpowiedzi (tekst albo base64) - do wysłania jako treść.</summary>
    public static byte[] BodyBytes(LinkResponse response) => BodyBytes(response.Body, response.BodyBase64);

    /// <summary>Bajty ciała żądania ze starej koperty (tekst albo base64).</summary>
    public static byte[] BodyBytes(LinkRequest request) => BodyBytes(request.Body, request.BodyBase64);

    private static byte[] BodyBytes(string? text, string? base64) =>
        base64 is { } b64 ? Convert.FromBase64String(b64)
        : text is { } plain ? Encoding.UTF8.GetBytes(plain)
        : [];

    // Biblioteka odrzuca LinkContent z pustym ContentType (ArgumentNullException). Prawdziwy typ i tak
    // jedzie w kopercie (metadanych), wiec tu wystarczy jakikolwiek niepusty domyslny.
    private const string DefaultContentType = "application/octet-stream";

    /// <summary>Żądanie z ciałem w pamięci (małe: exec, fs/list, zwykły zapis).</summary>
    public static LinkContent Request(LinkRequest envelope, ReadOnlyMemory<byte> body) =>
        LinkContent.FromBytes(body) with { Metadata = RequestMeta(envelope), ContentType = envelope.ContentType ?? DefaultContentType };

    /// <summary>Żądanie strumieniowe (upload): ciało płynie ze strumienia, nie z pamięci.</summary>
    public static LinkContent Request(LinkRequest envelope, Stream body, long? length, bool leaveOpen) =>
        LinkContent.FromStream(body, leaveOpen) with { Metadata = RequestMeta(envelope), ContentType = envelope.ContentType ?? DefaultContentType, Length = length };

    /// <summary>Odpowiedź nowego klienta (huba): status w metadanych, ciało jako treść.</summary>
    public static LinkContent Response(LinkResponse response) =>
        LinkContent.FromBytes(BodyBytes(response)) with { Metadata = ResponseMeta(response), ContentType = response.ContentType ?? DefaultContentType };

    /// <summary>Odpowiedź strumieniowa (download): przewijalny strumień z dysku o znanej długości, bez trzymania w pamięci.</summary>
    public static LinkContent Response(Stream body, LinkResponse envelope) =>
        LinkContent.FromStream(body) with { Metadata = ResponseMeta(envelope), ContentType = envelope.ContentType ?? DefaultContentType, Length = body.Length };

    /// <summary>Cała <see cref="LinkResponse"/> jako JSON w treści - dla starego klienta (avh-link), który czyta bajty odpowiedzi wprost.</summary>
    public static LinkContent LegacyResponse(LinkResponse response) =>
        LinkContent.FromBytes(LinkCodec.Encode(response)) with { ContentType = "application/json" };
}
