using Xunit;

namespace Playtest64.Launcher.Tests;

public class LauncherTests
{
    // These tests run the real thing against a fake server, on the system they run on. The fake server's
    // builds are for Linux, so on another system the list would be empty.
    private static bool OnLinux => OperatingSystem.IsLinux();

    [Fact]
    public async Task A_wrong_password_is_answered_with_the_servers_words_and_saves_nothing()
    {
        using var rig = new Rig(signedIn: false);

        var ex = await Assert.ThrowsAsync<LauncherException>(() => rig.Api.LoginAsync(FakeServer.Email, "nope"));

        Assert.Contains("falsch", ex.Message);
        Assert.False(rig.Settings.SignedIn);
    }

    [Fact]
    public async Task The_right_password_signs_in_asks_for_the_long_token_and_never_stores_the_password()
    {
        using var rig = new Rig(signedIn: false);

        await rig.Api.LoginAsync(FakeServer.Email, FakeServer.Password);

        Assert.True(rig.Settings.SignedIn);
        Assert.Contains("\"launcher\":true", rig.Server.LastLoginBody);

        var saved = File.ReadAllText(Path.Combine(rig.Dir, "settings.json"));
        Assert.Contains(FakeServer.Token, saved);
        Assert.DoesNotContain(FakeServer.Password, saved);
    }

    [Fact]
    public async Task The_list_has_the_builds_that_have_a_file_for_this_system_newest_first()
    {
        if (!OnLinux)
            return;

        using var rig = new Rig();

        var builds = await rig.Api.GetBuildsAsync();

        Assert.Equal(["0.2.1", "0.2.0"], builds.Select(b => b.Version));
        Assert.Equal("Neuer Build", builds[0].Notes);
    }

    [Fact]
    public async Task An_answer_with_numbers_as_text_and_missing_notes_is_read_all_the_same()
    {
        if (!OnLinux)
            return;

        using var rig = new Rig();

        // as the real server has been seen to answer: the size as a text, the notes null
        rig.Server.Builds.Clear();
        rig.Server.Builds.Add(new
        {
            id = "7", version = "v.0.2.1", notes = (string?)null,
            files = new object[] { new { id = "7", platform = "Linux", fileName = "GameA-linux-x64-v0.2.1.zip", fileSize = "123456", available = true } },
        });

        var builds = await rig.Api.GetBuildsAsync();

        Assert.Single(builds);
        Assert.Equal(7, builds[0].BuildId);
        Assert.Equal(123456, builds[0].Size);
        Assert.Equal("", builds[0].Notes);
    }

    [Fact]
    public async Task Installing_downloads_unpacks_marks_the_build_complete_and_makes_the_game_runnable()
    {
        if (!OnLinux)
            return;

        using var rig = new Rig();
        var build = (await rig.Api.GetBuildsAsync())[0];
        var last = 0.0;

        Assert.False(rig.Store.IsInstalled(build));

        await rig.Store.InstallAsync(build, new SynchronousProgress(v => last = v));

        Assert.True(rig.Store.IsInstalled(build));
        Assert.Equal(1.0, last, 3);
        Assert.Empty(Directory.GetFiles(Path.Combine(rig.Dir, "downloads")));			// the zip is cleaned up

        var game = Path.Combine(rig.Dir, "builds", "0.2.1", "GameA-linux-x64-v0.2.1", "game");
        Assert.True(OperatingSystem.IsLinux() && File.GetUnixFileMode(game).HasFlag(UnixFileMode.UserExecute));
        Assert.Equal(256 * 2000, new FileInfo(game + ".p64pak").Length);
    }

    [Fact]
    public async Task Playing_starts_the_game_in_its_own_folder()
    {
        if (!OnLinux)
            return;

        using var rig = new Rig();
        var build = (await rig.Api.GetBuildsAsync())[0];

        await rig.Store.InstallAsync(build);
        rig.Store.Play(build);

        var started = Path.Combine(rig.Dir, "builds", "0.2.1", "GameA-linux-x64-v0.2.1", "started.txt");

        for (var i = 0; i < 50 && !File.Exists(started); i++)
            await Task.Delay(100);

        Assert.True(File.Exists(started));
    }

    [Theory]
    [InlineData("auto", "graphics=none")]
    [InlineData("vulkan", "graphics=vulkan")]
    [InlineData("opengl", "graphics=opengl")]
    [InlineData("nonsense", "graphics=none")]
    public async Task The_game_is_told_which_graphics_api_to_start_with_and_auto_says_nothing(string choice, string expected)
    {
        if (!OnLinux)
            return;

        using var rig = new Rig();
        var build = (await rig.Api.GetBuildsAsync())[0];

        await rig.Store.InstallAsync(build);
        rig.Store.Play(build, choice);

        var written = Path.Combine(rig.Dir, "builds", "0.2.1", "GameA-linux-x64-v0.2.1", "graphics.txt");

        for (var i = 0; i < 50 && !File.Exists(written); i++)
            await Task.Delay(100);

        Assert.True(File.Exists(written));
        Assert.Equal(expected, File.ReadAllText(written).Trim());
    }

    [Fact]
    public void The_choice_is_kept_in_the_settings_and_a_stranger_in_the_file_means_auto()
    {
        using var rig = new Rig();

        Assert.Equal(Graphics.Auto, rig.Settings.GraphicsBackend);

        rig.Settings.GraphicsBackend = Graphics.OpenGl;
        rig.Settings.Save();

        Assert.Equal(Graphics.OpenGl, Settings.Load(rig.Dir).GraphicsBackend);
        Assert.False(Graphics.IsKnown("directx"));
        Assert.Equal(Graphics.All.Length, Graphics.Labels.Length);
    }

    [Fact]
    public async Task A_build_that_is_not_installed_cannot_be_played()
    {
        if (!OnLinux)
            return;

        using var rig = new Rig();
        var build = (await rig.Api.GetBuildsAsync())[0];

        Assert.Throws<LauncherException>(() => rig.Store.Play(build));
    }

    [Fact]
    public async Task A_zip_with_a_path_that_climbs_out_of_the_folder_is_refused()
    {
        if (!OnLinux)
            return;

        using var rig = new Rig();
        rig.Server.Zips[2] = FakeServer.MakeZip("0.2.1", "../escaped.txt");
        var build = (await rig.Api.GetBuildsAsync())[0];

        await Assert.ThrowsAsync<LauncherException>(() => rig.Store.InstallAsync(build));

        Assert.False(rig.Store.IsInstalled(build));
        Assert.False(File.Exists(Path.Combine(rig.Dir, "builds", "escaped.txt")));
    }

    [Fact]
    public async Task A_file_that_is_not_a_zip_is_refused_and_the_build_is_not_marked()
    {
        if (!OnLinux)
            return;

        using var rig = new Rig();
        rig.Server.Zips[2] = new byte[2000];
        var build = (await rig.Api.GetBuildsAsync())[0];

        await Assert.ThrowsAsync<LauncherException>(() => rig.Store.InstallAsync(build));

        Assert.False(rig.Store.IsInstalled(build));
    }

    [Fact]
    public async Task The_background_check_tells_of_a_new_build_once()
    {
        if (!OnLinux)
            return;

        using var rig = new Rig();

        var first = await UpdateCheck.RunAsync(rig.Settings, rig.Api, rig.Store);
        var second = await UpdateCheck.RunAsync(rig.Settings, rig.Api, rig.Store);

        Assert.Equal(UpdateKind.NewBuild, first.Kind);
        Assert.Equal("0.2.1", first.Build!.Version);
        Assert.Equal(UpdateKind.Nothing, second.Kind);
    }

    [Fact]
    public async Task The_background_check_says_nothing_about_a_build_that_is_installed()
    {
        if (!OnLinux)
            return;

        using var rig = new Rig();
        var build = (await rig.Api.GetBuildsAsync())[0];

        await rig.Store.InstallAsync(build);

        Assert.Equal(UpdateKind.Nothing, (await UpdateCheck.RunAsync(rig.Settings, rig.Api, rig.Store)).Kind);
    }

    [Fact]
    public async Task A_token_the_server_refuses_is_dropped_and_reported()
    {
        using var rig = new Rig();
        rig.Settings.Token = "stale";
        rig.Settings.Save();

        var result = await UpdateCheck.RunAsync(rig.Settings, rig.Api, rig.Store);

        Assert.Equal(UpdateKind.SignedOut, result.Kind);
        Assert.False(rig.Settings.SignedIn);
        Assert.DoesNotContain("stale", File.ReadAllText(Path.Combine(rig.Dir, "settings.json")));
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_is_offline_and_not_an_error()
    {
        using var rig = new Rig();
        rig.Server.Dispose();

        Assert.Equal(UpdateKind.Offline, (await UpdateCheck.RunAsync(rig.Settings, rig.Api, rig.Store)).Kind);
    }

    [Fact]
    public async Task Nobody_signed_in_means_nothing_to_check()
    {
        using var rig = new Rig(signedIn: false);

        Assert.Equal(UpdateKind.Nothing, (await UpdateCheck.RunAsync(rig.Settings, rig.Api, rig.Store)).Kind);
    }

    // Progress<T> posts to another thread, which a test cannot wait for; this one reports where it is called
    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
