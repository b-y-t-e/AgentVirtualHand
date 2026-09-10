using System.Text.Json;

namespace AgentVirtualHand.Services;

/// <summary>
/// Ustawienia zapamiętywane między uruchomieniami: %APPDATA%\AgentVirtualHand\settings.json.
/// Czytane i pisane ręcznie (JsonDocument / Utf8JsonWriter), żeby nie zależeć od refleksji -
/// aplikacja jest publikowana jako pojedynczy plik.
/// </summary>
public sealed record AppSettings(int? DurationMinutes)
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
                root.TryGetProperty("durationMinutes", out var dur) && dur.TryGetInt32(out var minutes)
                    ? minutes
                    : null);
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
            if (DurationMinutes is { } minutes) writer.WriteNumber("durationMinutes", minutes);
            writer.WriteEndObject();
        }
        catch
        {
            // Brak zapisu ustawien nie jest powodem, zeby przerywac prace.
        }
    }

    private static AppSettings Empty => new(DurationMinutes: null);

}
