using System.Text.Json;
using System.Text.Json.Serialization;

namespace Playtest64.Launcher;

// Written out at build time rather than found by reflection at run time, which a trimmed program cannot do
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
internal sealed partial class SettingsJson : JsonSerializerContext;

/// <summary>
/// What the launcher remembers: who is signed in (the address and the token, never the password) and the
/// newest build the person has been told about, so that a notification comes once.
/// </summary>
public sealed class Settings
{
    public const string DefaultServer = "https://playtest64.xyz";

    public string Email { get; set; } = "";
    public string Token { get; set; } = "";
    public int AnnouncedBuild { get; set; }

    /// <summary>The newest launcher version the person has been told about.</summary>
    public string AnnouncedLauncher { get; set; } = "";

    /// <summary>The graphics API the game is started with: "auto" (the game decides: Vulkan, OpenGL where there is no Vulkan), "vulkan" or "opengl".</summary>
    public string GraphicsBackend { get; set; } = Graphics.Auto;

    /// <summary>Whether the look follows the season (Halloween in October, and so on), as on the website.</summary>
    public bool SeasonalLook { get; set; } = true;

    /// <summary>Not saved: PLAYTEST_SERVER (for testing against a local server) or the real one.</summary>
    [JsonIgnore]
    public string Server { get; set; } = DefaultServer;

    [JsonIgnore]
    private string _path = "";

    [JsonIgnore]
    public bool SignedIn => Token.Length > 0;

    public static Settings Load(string dataDir)
    {
        var path = Path.Combine(dataDir, "settings.json");
        var settings = new Settings();

        try
        {
            if (File.Exists(path))
                settings = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJson.Default.Settings) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // an unreadable file is the same as none: the person signs in again
        }

        settings._path = path;

        var server = Environment.GetEnvironmentVariable("PLAYTEST_SERVER");
        settings.Server = string.IsNullOrEmpty(server) ? DefaultServer : server.TrimEnd('/');

        return settings;
    }

    public void Save()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(this, SettingsJson.Default.Settings));

        // the token is a key to the downloads: nobody else on the machine has any business reading it
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void SignOut()
    {
        Token = "";
        Save();
    }
}
