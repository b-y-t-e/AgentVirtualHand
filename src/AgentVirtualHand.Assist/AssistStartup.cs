using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AgentVirtualHand.Assist;

/// <summary>Opt-in startup for the current Windows user; the Run value is the source of truth.</summary>
internal sealed class AssistStartup
{
    private const string ValueName = "avh-assist";
    private readonly string _runKeyPath;

    internal AssistStartup(string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
        => _runKeyPath = runKeyPath;

    public static bool IsSupported => OperatingSystem.IsWindows();

    [SupportedOSPlatform("windows")]
    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath);
        return key?.GetValue(ValueName) is string command && command == CurrentCommand();
    }

    [SupportedOSPlatform("windows")]
    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true);
            key.SetValue(ValueName, CurrentCommand(), RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string CurrentCommand()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate the application executable.");
        // Also support a framework-dependent launch through `dotnet avh-assist.dll`.
        return Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $"\"{executable}\" \"{Assembly.GetEntryAssembly()!.Location}\""
            : $"\"{executable}\"";
    }
}
