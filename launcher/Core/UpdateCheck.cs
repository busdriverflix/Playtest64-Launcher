namespace Playtest64.Launcher;

public enum UpdateKind
{
    Nothing,
    NewBuild,
    SignedOut,
    Offline,
}

public sealed record UpdateResult(UpdateKind Kind, BuildInfo? Build = null);

/// <summary>
/// The question the background check asks: is there a build the person has not been told about and does
/// not have? Asked at most once per build: announcing it writes it down.
/// </summary>
public static class UpdateCheck
{
    public static async Task<UpdateResult> RunAsync(Settings settings, LauncherApi api, BuildStore store, CancellationToken ct = default)
    {
        if (!settings.SignedIn)
            return new UpdateResult(UpdateKind.Nothing);

        IReadOnlyList<BuildInfo> builds;

        try
        {
            builds = await api.GetBuildsAsync(ct);
        }
        catch (SignedOutException)
        {
            return new UpdateResult(UpdateKind.SignedOut);
        }
        catch (Exception)
        {
            // no network, or an answer it cannot read: either way, try again later
            return new UpdateResult(UpdateKind.Offline);
        }

        var newest = builds.FirstOrDefault();

        if (newest == null || newest.BuildId == settings.AnnouncedBuild || store.IsInstalled(newest))
            return new UpdateResult(UpdateKind.Nothing);

        settings.AnnouncedBuild = newest.BuildId;
        settings.Save();

        return new UpdateResult(UpdateKind.NewBuild, newest);
    }
}
