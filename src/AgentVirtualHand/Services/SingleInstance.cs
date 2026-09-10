using System.Runtime.InteropServices;

namespace AgentVirtualHand.Services;

/// <summary>
/// Zapewnia, że dana aplikacja działa w jednej kopii na sesję użytkownika. Dwie instancje
/// hosta biłyby się o tożsamość węzła Tailcata i plik parowania, dwa huby o wspólną listę
/// połączeń - w obu przypadkach druga kopia szkodzi, a nie pomaga.
/// Zakres to sesja (prefiks "Local\"), więc dwie osoby zalogowane przez RDP mają każda swoją.
/// </summary>
public static class SingleInstance
{
    private static Mutex? _held;

    /// <summary>
    /// Zajmuje globalny uchwyt o podanej nazwie. Zwraca false, gdy inna kopia już go trzyma -
    /// wtedy trzeba zakończyć proces. Uchwyt żyje do końca procesu (zwalnia go system przy wyjściu).
    /// </summary>
    public static bool TryAcquire(string name)
    {
        // Mutex nazwany na poziomie sesji. "Local\" jest domyślne, ale podajemy wprost dla jasności.
        var mutex = new Mutex(initiallyOwned: false, $"Local\\{name}");

        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // Poprzednia kopia padła bez zwolnienia - i tak jesteśmy teraz jedyni.
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            return false;
        }

        _held = mutex;
        return true;
    }

    /// <summary>Okienko na Windowsie z informacją, czemu druga kopia się nie otworzyła.</summary>
    public static void WarnAlreadyRunning(string title, string message)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        try { MessageBox(IntPtr.Zero, message, title, 0x00000040 /* MB_ICONINFORMATION */); }
        catch { /* brak GUI/USER32 - trudno, i tak konczymy */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
