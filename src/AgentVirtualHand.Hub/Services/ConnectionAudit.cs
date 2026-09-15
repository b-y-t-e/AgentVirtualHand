namespace AgentVirtualHand.Hub.Services;

/// <summary>Log jednego polaczenia: kazdy wpis z nazwa maszyny i przyciety do jednej linii.</summary>
internal sealed class ConnectionAudit(Func<string> connectionName, Action<string, string> sink)
{
    private const int MaxTextLength = 200;

    public void Write(string kind, string text) => sink(kind, $"{connectionName()}: {Cap(text)}");

    public void Note(string? note)
    {
        if (!string.IsNullOrWhiteSpace(note)) Write("note", note);
    }

    private static string Cap(string text)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= MaxTextLength ? text : text[..MaxTextLength] + "...";
    }
}
