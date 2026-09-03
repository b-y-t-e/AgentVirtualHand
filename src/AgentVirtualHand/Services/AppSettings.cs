using System.Text.Json;

namespace AgentVirtualHand.Services;

/// <summary>
/// Ustawienia zapamiętywane między uruchomieniami: %APPDATA%\AgentVirtualHand\settings.json.
/// Czytane i pisane ręcznie (JsonDocument / Utf8JsonWriter), żeby nie zależeć od refleksji -
/// aplikacja jest publikowana jako pojedynczy plik.
/// </summary>
public sealed record AppSettings(string? Port, bool? LanVisible, int? DurationMinutes, string? Address)
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AgentVirtualHand", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return Empty;

            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;

            return new AppSettings(
                Text(root, "port"),
                root.TryGetProperty("lanVisible", out var lan) && lan.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? lan.GetBoolean()
                    : null,
                root.TryGetProperty("durationMinutes", out var dur) && dur.TryGetInt32(out var minutes)
                    ? minutes
                    : null,
                Text(root, "address"));
        }
        catch
        {
            return Empty; // uszkodzony plik nie moze blokowac startu aplikacji
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            using var stream = File.Create(FilePath);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();
            if (Port is not null) writer.WriteString("port", Port);
            if (LanVisible is { } lan) writer.WriteBoolean("lanVisible", lan);
            if (DurationMinutes is { } minutes) writer.WriteNumber("durationMinutes", minutes);
            if (Address is not null) writer.WriteString("address", Address);
            writer.WriteEndObject();
        }
        catch
        {
            // Brak zapisu ustawien nie jest powodem, zeby przerywac prace.
        }
    }

    private static AppSettings Empty => new(null, null, null, null);

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
