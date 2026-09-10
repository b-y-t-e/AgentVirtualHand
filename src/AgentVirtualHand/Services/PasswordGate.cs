using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentVirtualHand.Services;

/// <summary>
/// Hasło chroniące okno aplikacji. Trzymany jest wyłącznie skrót PBKDF2 z solą -
/// z pliku nie da się odczytać hasła, a porównanie jest odporne na atak czasowy.
/// Aplikacja nie ma hasła domyślnego: pierwsze uruchomienie wymusza ustawienie własnego,
/// bo hasło wpisane w kod znałby każdy, kto pobierze plik.
/// </summary>
public sealed class PasswordGate
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;

    private readonly string _file;

    public PasswordGate(string directory) => _file = Path.Combine(directory, "lock.json");

    public bool IsConfigured => Read() is not null;

    /// <summary>Ustawia hasło przy pierwszym uruchomieniu albo zmienia istniejące.</summary>
    public void Set(string password)
    {
        if (password.Length < 4) throw new ArgumentException("Password must be at least 4 characters.");

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Derive(password, salt);

        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);

        using var stream = File.Create(_file);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteNumber("iterations", Iterations);
        writer.WriteString("salt", Convert.ToBase64String(salt));
        writer.WriteString("key", Convert.ToBase64String(key));
        writer.WriteEndObject();
    }

    public bool Verify(string password)
    {
        if (Read() is not { } stored) return false;

        var attempt = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), stored.Salt, stored.Iterations, HashAlgorithmName.SHA256, stored.Key.Length);

        return CryptographicOperations.FixedTimeEquals(attempt, stored.Key);
    }

    private static byte[] Derive(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(
        Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, KeyBytes);

    private (int Iterations, byte[] Salt, byte[] Key)? Read()
    {
        try
        {
            if (!File.Exists(_file)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(_file));
            var root = doc.RootElement;

            var iterations = root.GetProperty("iterations").GetInt32();
            var salt = Convert.FromBase64String(root.GetProperty("salt").GetString()!);
            var key = Convert.FromBase64String(root.GetProperty("key").GetString()!);

            return (iterations, salt, key);
        }
        catch
        {
            // Uszkodzony plik traktujemy jak brak hasla - aplikacja poprosi o ustawienie nowego.
            return null;
        }
    }
}
