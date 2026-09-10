using System.Text.Json;

namespace AgentVirtualHand.Hub.Services;

/// <summary>
/// Zapisane połączenie do jednej maszyny. Każde ma własny katalog sparowania
/// (osobna tożsamość Tailcata) i własny port na loopbacku.
/// </summary>
public sealed record ConnectionEntry(string Id, string Name, int Port, bool Enabled, string Token)
{
    /// <summary>Katalog ze sparowaniem tej jednej maszyny - stąd bierze się izolacja połączeń.</summary>
    public string StoreRoot => Path.Combine(HubPaths.Root, "links", Id);
}

public static class HubPaths
{
    // Nazwa na dysku celowo nieczytelna, z jednorazowa migracja z poprzedniej wersji.
    public static string Root { get; } =
        AgentVirtualHand.Services.AppPaths.Resolve("avh-hub", "AgentVirtualHand.Hub");

    public static string ConnectionsFile => Path.Combine(Root, "connections.json");
}

/// <summary>
/// Lista połączeń w %APPDATA%\AgentVirtualHand.Hub\connections.json.
/// Czytana i pisana ręcznie, bez refleksji - aplikacja jest publikowana jako pojedynczy plik.
/// </summary>
public static class ConnectionStore
{
    public static List<ConnectionEntry> Load()
    {
        try
        {
            if (!File.Exists(HubPaths.ConnectionsFile)) return [];

            using var doc = JsonDocument.Parse(File.ReadAllText(HubPaths.ConnectionsFile));
            var result = new List<ConnectionEntry>();

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var id = Text(item, "id");
                if (id is null) continue;

                result.Add(new ConnectionEntry(
                    id,
                    Text(item, "name") ?? id,
                    item.TryGetProperty("port", out var port) && port.TryGetInt32(out var value) ? value : 0,
                    !item.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.False,
                    Text(item, "token") ?? ""));
            }

            return result;
        }
        catch
        {
            return []; // uszkodzony plik nie moze blokowac startu aplikacji
        }
    }

    public static void Save(IEnumerable<ConnectionEntry> connections)
    {
        try
        {
            Directory.CreateDirectory(HubPaths.Root);

            using var stream = File.Create(HubPaths.ConnectionsFile);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartArray();
            foreach (var c in connections)
            {
                writer.WriteStartObject();
                writer.WriteString("id", c.Id);
                writer.WriteString("name", c.Name);
                writer.WriteNumber("port", c.Port);
                writer.WriteBoolean("enabled", c.Enabled);
                writer.WriteString("token", c.Token);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        catch
        {
            // Brak zapisu listy nie jest powodem, zeby przerywac prace.
        }
    }

    /// <summary>Usuwa też sparowanie - inaczej wpis wróciłby przy ponownym dodaniu z tym samym id.</summary>
    public static void Forget(ConnectionEntry connection)
    {
        try
        {
            if (Directory.Exists(connection.StoreRoot)) Directory.Delete(connection.StoreRoot, recursive: true);
        }
        catch
        {
            // Sparowanie zostanie na dysku, ale wpis i tak znika z listy.
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
