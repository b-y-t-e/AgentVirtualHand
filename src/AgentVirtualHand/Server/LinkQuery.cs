using Microsoft.AspNetCore.WebUtilities;

namespace AgentVirtualHand.Server;

/// <summary>
/// Czyta parametr z query stringu koperty tak samo jak binding ASP.NET ('+' = spacja, %XX),
/// zeby transfer strumieniowy widzial dokladnie te sciezke, ktora zobaczylby endpoint na loopbacku.
/// Wspoldzielone host&lt;-&gt;hub.
/// </summary>
public static class LinkQuery
{
    public static string? Value(string? query, string name) =>
        QueryHelpers.ParseNullableQuery(query) is { } parsed && parsed.TryGetValue(name, out var values)
            ? values.ToString()
            : null;

    /// <summary>Ścieżka pliku transferu albo null, gdy jej brak lub jest pusta - wtedy odpowiedzią jest 400.</summary>
    public static string? FilePath(string? query) =>
        Value(query, "path") is { } path && !string.IsNullOrWhiteSpace(path) ? path : null;
}
