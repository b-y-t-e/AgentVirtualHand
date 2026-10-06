using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentVirtualHand.Assist;

/// <summary>
/// Usuwa wlasny plik .exe i lezacy obok msquic.dll po zamknieciu programu. Wlasny exe jest w trakcie
/// dzialania zablokowany, wiec kasowanie robi osobny, odlaczony proces: czeka, az nasz proces zniknie,
/// kasuje oba pliki (i pusty folder), po czym kasuje sam siebie. Zakres jest waski - tylko te dwa pliki.
/// </summary>
internal static class SelfWipe
{
    /// <summary>
    /// Magiczne slowo wpisywane w oknie. Trzymane w zmiennej srodowiskowej, nie w kodzie - repo jest
    /// publiczne, wiec slowo wpisane na stale nie byloby tajne. Brak zmiennej = funkcja wylaczona.
    /// </summary>
    public static string? Word
    {
        get
        {
            var w = Environment.GetEnvironmentVariable("AVH_ASSIST_WIPEWORD");
            return string.IsNullOrWhiteSpace(w) ? null : w.Trim().ToLowerInvariant();
        }
    }

    public static bool Enabled => Word is not null;

    /// <summary>Zaplanuj skasowanie plikow po wyjsciu procesu. Woalne tuz przed zamknieciem aplikacji.</summary>
    public static void Schedule()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        var dir = Path.GetDirectoryName(exe)!;
        var dll = Path.Combine(dir, "msquic.dll");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            ScheduleWindows(exe, dll, dir);
        else
            SchedulePosix(exe, dll);
    }

    private static void ScheduleWindows(string exe, string dll, string dir)
    {
        // Petla kasuje exe, az sie uda (po wyjsciu naszego procesu zwalnia sie blokada), max ~2 min.
        // Potem dll, pusty folder (rd kasuje tylko pusty) i na koncu sam skrypt.
        var script = $"""
            @echo off
            setlocal
            for /l %%i in (1,1,60) do (
              del /f /q "{exe}" >nul 2>&1
              if not exist "{exe}" goto done
              ping 127.0.0.1 -n 2 >nul
            )
            :done
            del /f /q "{dll}" >nul 2>&1
            rd "{dir}" >nul 2>&1
            del /f /q "%~f0" >nul 2>&1
            """;

        var bat = Path.Combine(Path.GetTempPath(), $"~{Guid.NewGuid():n}.cmd");
        File.WriteAllText(bat, script, new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private static void SchedulePosix(string exe, string dll)
    {
        // exe na Unix nie jest blokowany, ale i tak dajemy chwile, zeby proces zdazyl wyjsc.
        var cmd = $"sleep 1; rm -f '{exe}' '{dll}'";
        Process.Start(new ProcessStartInfo("/bin/sh", $"-c \"{cmd}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
}
