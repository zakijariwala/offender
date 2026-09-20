using Microsoft.Win32;

namespace Offender;

/// <summary>
/// Start-with-Windows via the per-user Run key. HKCU only and only ever toggled from the
/// context menu -- nothing here touches machine-wide state or needs elevation.
/// </summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Offender";

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (!enabled) { key.DeleteValue(ValueName, throwOnMissingValue: false); return; }

            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    /// <summary>
    /// Drops a shortcut into the user's Start Menu so the app can be pinned to the
    /// taskbar. Windows has not allowed programmatic pinning since Windows 8, so making
    /// the app appear in Start -- where "Pin to taskbar" lives -- is the real mechanism.
    /// </summary>
    public static bool CreateStartMenuShortcut()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            Directory.CreateDirectory(dir);

            string link = Path.Combine(dir, "Offender.lnk");
            return Native.ShellLink.Create(link, exe, "Lightweight system monitor");
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }
}
