namespace Playtest64.Launcher;

/// <summary>Everything the interface and the background check share: the settings, the server, the builds on disk.</summary>
public sealed class LauncherContext
{
    public Settings Settings { get; }
    public LauncherApi Api { get; }
    public BuildStore Store { get; }

    public LauncherContext()
    {
        var dataDir = Platform.DataDir;
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        // a server that tells a client who it is gets a name that says what this is, and nothing else
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Playtest64-Launcher/1.0");

        Settings = Settings.Load(dataDir);
        Api = new LauncherApi(http, Settings);
        Store = new BuildStore(dataDir, Api);
    }
}
