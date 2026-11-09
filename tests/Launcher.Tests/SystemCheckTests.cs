using Xunit;

namespace Playtest64.Launcher.Tests;

public class SystemCheckTests
{
	private static SystemReport Missing(VulkanState state, params string[] libraries) => new(libraries, state, null);

	[Theory]
	[InlineData("ID=ubuntu\nID_LIKE=debian\n", "apt", "apt-get update && apt-get install -y libglfw3 libvulkan1")]
	[InlineData("ID=linuxmint\nID_LIKE=\"ubuntu debian\"\n", "apt", "apt-get update && apt-get install -y libglfw3 libvulkan1")]
	[InlineData("ID=fedora\n", "dnf", "dnf install -y glfw vulkan-loader")]
	[InlineData("ID=manjaro\nID_LIKE=arch\n", "pacman", "pacman -S --needed --noconfirm glfw vulkan-icd-loader")]
	[InlineData("ID=\"opensuse-tumbleweed\"\nID_LIKE=\"opensuse suse\"\n", "zypper", "zypper --non-interactive install libglfw3 libvulkan1")]
	public void The_libraries_are_installed_with_the_package_manager_of_the_distribution(string osRelease, string manager, string command)
	{
		var plan = InstallPlan.For(osRelease, Missing(VulkanState.NoLoader, "libglfw.so.3", "libvulkan.so.1"));

		Assert.NotNull(plan);
		Assert.Equal(manager, plan!.Manager);
		Assert.StartsWith(command, plan.Command);
	}

	[Fact]
	public void Only_what_is_missing_is_asked_for()
	{
		var plan = InstallPlan.For("ID=debian\n", Missing(VulkanState.Ok, "libglfw.so.3"));

		Assert.Equal("apt-get update && apt-get install -y libglfw3", plan!.Command);
	}

	[Fact]
	public void A_missing_driver_brings_the_vulkan_driver_packages_and_nvidia_is_left_alone()
	{
		var apt = InstallPlan.For("ID=ubuntu\n", Missing(VulkanState.NoDevice));
		var arch = InstallPlan.For("ID=arch\n", Missing(VulkanState.SoftwareOnly));

		Assert.Equal("apt-get update && apt-get install -y mesa-vulkan-drivers", apt!.Command);
		Assert.Equal("pacman -S --needed --noconfirm vulkan-radeon vulkan-intel", arch!.Command);
		Assert.DoesNotContain("nvidia", apt.Command + arch.Command);
	}

	[Fact]
	public void Nothing_missing_means_nothing_to_install()
	{
		Assert.Null(InstallPlan.For("ID=ubuntu\n", Missing(VulkanState.Ok)));
	}

	[Fact]
	public void A_distribution_that_is_not_known_gets_no_plan()
	{
		Assert.Null(InstallPlan.For("ID=nixos\n", Missing(VulkanState.NoLoader, "libvulkan.so.1")));
	}

	[Fact]
	public void The_report_says_what_is_wrong_and_a_good_machine_says_nothing()
	{
		Assert.False(Missing(VulkanState.Ok).HasProblem);
		Assert.False(Missing(VulkanState.Unknown).HasProblem);
		Assert.True(Missing(VulkanState.SoftwareOnly).HasProblem);
		Assert.Contains("libglfw.so.3", Missing(VulkanState.Ok, "libglfw.so.3").Describe()[0]);
		Assert.Contains("llvmpipe", new SystemReport([], VulkanState.SoftwareOnly, "llvmpipe").Describe()[0]);
	}

	[Fact]
	public void The_check_runs_on_this_machine_without_throwing()
	{
		var report = SystemCheck.Run();

		Assert.NotNull(report.MissingLibraries);
	}
}
