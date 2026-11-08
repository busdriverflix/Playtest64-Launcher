namespace Playtest64.Launcher;

public enum UpdateKind
{
    Nothing,
    NewBuild,
    NewLauncher,
    NewBoth,
    SignedOut,
    Offline,
}

public sealed record UpdateResult(UpdateKind Kind, BuildInfo? Build = null, LauncherRelease? Launcher = null);

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
        BuildInfo? fresh = null;

        if (newest != null && newest.BuildId != settings.AnnouncedBuild && !store.IsInstalled(newest))
            fresh = newest;

        var launcher = await NewerLauncherAsync(settings, api, ct);

        if (fresh == null && launcher == null)
            return new UpdateResult(UpdateKind.Nothing);

        if (fresh != null)
            settings.AnnouncedBuild = fresh.BuildId;

        if (launcher != null)
            settings.AnnouncedLauncher = launcher.Version;

        settings.Save();

        return new UpdateResult(fresh != null && launcher != null ? UpdateKind.NewBoth : fresh != null ? UpdateKind.NewBuild : UpdateKind.NewLauncher, fresh, launcher);
    }

    /// <summary>The launcher the server offers if it is newer than this one and the person has not been told of it yet. Never fails: a server without the route simply has none.</summary>
    private static async Task<LauncherRelease?> NewerLauncherAsync(Settings settings, LauncherApi api, CancellationToken ct)
    {
        try
        {
            var release = await api.GetLauncherReleaseAsync(ct);

            return release != null && SelfUpdate.IsNewer(release.Version) && release.Version != settings.AnnouncedLauncher ? release : null;
        }
        catch (LauncherException)
        {
            return null;
        }
    }
}
