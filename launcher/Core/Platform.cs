namespace Playtest64.Launcher;

/// <summary>What differs between the two systems the launcher runs on.</summary>
public static class Platform
{
    /// <summary>The name the website gives the platform of a build file.</summary>
    public static string Name => OperatingSystem.IsWindows() ? "Windows" : "Linux";

    /// <summary>The program that starts a build, inside the folder the build's zip unpacks into.</summary>
    public static string GameProgram => OperatingSystem.IsWindows() ? "game.exe" : "game";

    /// <summary>
    /// Where the launcher keeps its settings, downloads and installed builds. PLAYTEST_LAUNCHER_HOME moves it
    /// (the tests use that).
    /// </summary>
    public static string DataDir
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("PLAYTEST_LAUNCHER_HOME");

            var dir = !string.IsNullOrEmpty(overridden)
                ? overridden
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Playtest64Launcher");

            Directory.CreateDirectory(dir);

            return dir;
        }
    }
}
