using Microsoft.Win32;

namespace BTDeviceBattery.Services;

/// <summary>Run-at-startup via the HKCU Run key (works for a portable exe).</summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BTDeviceBattery";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is not null;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            // ProcessPath, not Assembly.Location: empty under single-file publish.
            var exe = Environment.ProcessPath;
            if (exe is not null) key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}