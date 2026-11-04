using Microsoft.Win32;

namespace Playtest64.Launcher;

/// <summary>
/// Whether the background check starts with the person's login: a "Run" entry in the registry on Windows,
/// an autostart entry on Linux. Neither needs administrator rights.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Playtest64Launcher";

    private static string DesktopFile
    {
        get
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

            if (string.IsNullOrEmpty(config))
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

            return Path.Combine(config, "autostart", "playtest64-launcher.desktop");
        }
    }

    public static bool IsEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) != null;
        }

        return File.Exists(DesktopFile);
    }

    public static void SetEnabled(bool on)
    {
        var self = Environment.ProcessPath ?? throw new LauncherException("Der Pfad des Launchers ist unbekannt.");

        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);

            if (on)
                key.SetValue(RunValue, $"\"{self}\" --background");
            else
                key.DeleteValue(RunValue, throwOnMissingValue: false);

            return;
        }

        if (!on)
        {
            File.Delete(DesktopFile);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DesktopFile)!);

        // "Exec" quotes the path in case it has a space in it
        File.WriteAllText(DesktopFile,
            "[Desktop Entry]\nType=Application\nName=Playtest64 Launcher (Update-Check)\n" +
            $"Exec=\"{self}\" --background\nNoDisplay=true\nX-GNOME-Autostart-enabled=true\n");
    }
}
