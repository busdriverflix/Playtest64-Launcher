using Xunit;

namespace Playtest64.Launcher.Tests;

public class SelfUpdateTests
{
	[Theory]
	[InlineData("9.0.0", true)]
	[InlineData("v9.0.0", true)]
	[InlineData("0.0.1", false)]
	[InlineData("garbage", false)]
	[InlineData("", false)]
	public void A_higher_version_is_newer_and_nothing_else_is(string server, bool newer)
	{
		Assert.Equal(newer, SelfUpdate.IsNewer(server));
	}

	[Fact]
	public void The_own_version_is_not_newer_than_itself()
	{
		Assert.False(SelfUpdate.IsNewer(SelfUpdate.CurrentText));
	}

	[Fact]
	public async Task The_check_tells_of_a_newer_launcher_once()
	{
		using var rig = new Rig();
		rig.Server.LauncherVersion = "9.0.0";

		var first = await UpdateCheck.RunAsync(rig.Settings, rig.Api, rig.Store);
		var second = await UpdateCheck.RunAsync(rig.Settings, rig.Api, rig.Store);

		Assert.Equal("9.0.0", first.Launcher!.Version);
		Assert.True(first.Kind is UpdateKind.NewLauncher or UpdateKind.NewBoth);
		Assert.Null(second.Launcher);
	}

	[Fact]
	public async Task A_launcher_that_is_not_newer_is_not_announced()
	{
		using var rig = new Rig();
		rig.Server.LauncherVersion = SelfUpdate.CurrentText;

		var result = await UpdateCheck.RunAsync(rig.Settings, rig.Api, rig.Store);

		Assert.Null(result.Launcher);
	}

	[Fact]
	public async Task The_check_says_which_version_this_launcher_is()
	{
		using var rig = new Rig();
		rig.Server.LauncherVersion = "9.0.0";

		await rig.Api.GetLauncherReleaseAsync();

		Assert.Equal(SelfUpdate.Current.ToString(3), rig.Server.LauncherCheckVersion);
	}

	[Fact]
	public async Task The_new_file_takes_the_old_ones_place_and_the_old_one_is_kept_beside_it()
	{
		using var rig = new Rig();
		rig.Server.LauncherVersion = "9.0.0";
		var target = Path.Combine(rig.Dir, "Playtest64Launcher");
		File.WriteAllText(target, "old");

		var release = (await rig.Api.GetLauncherReleaseAsync())!;
		await SelfUpdate.ApplyAsync(rig.Api, release, target);

		Assert.Equal(rig.Server.LauncherFile, File.ReadAllBytes(target));
		Assert.Equal("old", File.ReadAllText(target + ".old"));
		Assert.False(File.Exists(target + ".new"));

		if (!OperatingSystem.IsWindows())
			Assert.True(File.GetUnixFileMode(target).HasFlag(UnixFileMode.UserExecute));
	}

	[Fact]
	public async Task A_download_that_does_not_match_the_hash_changes_nothing()
	{
		using var rig = new Rig();
		rig.Server.LauncherVersion = "9.0.0";
		rig.Server.LauncherHash = new string('0', 64);
		var target = Path.Combine(rig.Dir, "Playtest64Launcher");
		File.WriteAllText(target, "old");

		var release = (await rig.Api.GetLauncherReleaseAsync())!;

		await Assert.ThrowsAsync<LauncherException>(() => SelfUpdate.ApplyAsync(rig.Api, release, target));

		Assert.Equal("old", File.ReadAllText(target));
		Assert.False(File.Exists(target + ".new"));
		Assert.False(File.Exists(target + ".old"));
	}

	[Fact]
	public async Task A_server_without_a_launcher_is_no_launcher_update_and_no_error()
	{
		using var rig = new Rig();

		Assert.Null(await rig.Api.GetLauncherReleaseAsync());
	}
}
