namespace AgentVirtualHand.Services;

/// <summary>
/// Katalog danych aplikacji. Nazwa na dysku jest celowo nieczytelna ("avh"), a nie pełna
/// nazwa produktu - żeby ktoś przeglądający %APPDATA% nie odgadł, do czego służy narzędzie.
/// Przy pierwszym uruchomieniu przenosi stary katalog, więc hasło, ustawienia i sparowania
/// z poprzedniej wersji nie przepadają.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> _root = new(() => Resolve("avh", "AgentVirtualHand"));

    public static string Root => _root.Value;

    /// <summary>
    /// Zwraca katalog o nowej nazwie, jednorazowo przenosząc do niego stary, jeśli tylko on istnieje.
    /// </summary>
    internal static string Resolve(string current, string legacy)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var path = Path.Combine(appData, current);
        var old = Path.Combine(appData, legacy);

        try
        {
            if (!Directory.Exists(path) && Directory.Exists(old)) Directory.Move(old, path);
        }
        catch
        {
            // Nieudana migracja nie moze blokowac startu - aplikacja utworzy nowy, pusty katalog.
        }

        return path;
    }
}
