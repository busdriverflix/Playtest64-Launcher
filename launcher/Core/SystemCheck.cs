using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Playtest64.Launcher;

/// <summary>What the Vulkan check found on this machine.</summary>
public enum VulkanState
{
	Ok,				// a real graphics card with Vulkan
	SoftwareOnly,	// only an emulation on the processor: it runs, but very slowly
	NoDevice,		// the loader is there but no graphics card answered: a driver is missing, or the card is too old
	NoLoader,		// the Vulkan loader library is not installed
	Unknown,		// the probe itself failed; the game is not stopped for that
}

/// <summary>
/// What the game needs from the machine, looked at before it is started. The game itself is linked against GLFW and the Vulkan
/// loader (Linux), and a graphics card with a Vulkan driver; on Windows the zip carries GLFW and only the driver can be missing.
/// </summary>
public sealed record SystemReport(IReadOnlyList<string> MissingLibraries, VulkanState Vulkan, string? Device)
{
	public bool AllRight => MissingLibraries.Count == 0 && Vulkan is VulkanState.Ok or VulkanState.Unknown;

	public bool HasProblem => MissingLibraries.Count > 0 || Vulkan is VulkanState.NoDevice or VulkanState.NoLoader or VulkanState.SoftwareOnly;

	/// <summary>The problem in words for the person, one line each.</summary>
	public IReadOnlyList<string> Describe()
	{
		var lines = new List<string>();

		foreach (var library in MissingLibraries)
			lines.Add($"Es fehlt die Bibliothek {library}.");

		switch (Vulkan)
		{
			case VulkanState.NoDevice:
				lines.Add("Keine Grafikkarte mit Vulkan gefunden. Das Spiel startet dann mit OpenGL, das auch auf älteren Karten läuft. Mit einem Vulkan-Treiber läuft es meist besser.");
				break;

			case VulkanState.SoftwareOnly:
				lines.Add($"Nur eine Software-Grafik ({Device}) gefunden. Das Spiel startet, läuft aber sehr langsam. Meist fehlt der Grafiktreiber, oder es ist eine virtuelle Maschine.");
				break;
		}

		return lines;
	}
}

public static class SystemCheck
{
	/// <summary>The libraries a Linux game needs that may be missing. (Windows has them in the zip.)</summary>
	public static readonly string[] LinuxLibraries = ["libglfw.so.3", "libvulkan.so.1"];

	public static SystemReport Run()
	{
		var missing = new List<string>();

		if (OperatingSystem.IsLinux())
		{
			foreach (var name in LinuxLibraries)
			{
				if (!NativeLibrary.TryLoad(name, out var handle))
					missing.Add(name);
				else
					NativeLibrary.Free(handle);
			}
		}

		if (missing.Contains("libvulkan.so.1"))
			return new SystemReport(missing, VulkanState.NoLoader, null);

		var (state, device) = ProbeVulkan();

		return new SystemReport(missing, state, device);
	}

	private static string LoaderName => OperatingSystem.IsWindows() ? "vulkan-1.dll" : "libvulkan.so.1";

	/// <summary>
	/// Makes a Vulkan instance, asks for the graphics cards and the kind of each, the way the game's first steps do, and
	/// lets everything go again. Written against the raw functions so that nothing has to be installed to ask.
	/// </summary>
	private static unsafe (VulkanState, string?) ProbeVulkan()
	{
		try
		{
			if (!NativeLibrary.TryLoad(LoaderName, out var loader))
				return (VulkanState.NoLoader, null);

			try
			{
				if (!NativeLibrary.TryGetExport(loader, "vkGetInstanceProcAddr", out var getProc))
					return (VulkanState.Unknown, null);

				var vkGetInstanceProcAddr = (delegate* unmanaged<nint, byte*, nint>)getProc;

				var createInstance = (delegate* unmanaged<byte*, nint, nint*, int>)GetProc(vkGetInstanceProcAddr, 0, "vkCreateInstance");

				if (createInstance == null)
					return (VulkanState.Unknown, null);

				// VkInstanceCreateInfo with nothing set but its type (INSTANCE_CREATE_INFO = 1): 64 bytes on a 64-bit system
				var info = stackalloc byte[128];
				new Span<byte>(info, 128).Clear();
				*(int*)info = 1;

				nint instance = 0;

				if (createInstance(info, 0, &instance) != 0)
				{
					// -9 (incompatible driver) and the like: the loader is there but nothing answers to it
					return (VulkanState.NoDevice, null);
				}

				try
				{
					var enumerate = (delegate* unmanaged<nint, uint*, nint*, int>)GetProc(vkGetInstanceProcAddr, instance, "vkEnumeratePhysicalDevices");
					var getProperties = (delegate* unmanaged<nint, byte*, void>)GetProc(vkGetInstanceProcAddr, instance, "vkGetPhysicalDeviceProperties");
					var enumerateExtensions = (delegate* unmanaged<nint, byte*, uint*, byte*, int>)GetProc(vkGetInstanceProcAddr, instance, "vkEnumerateDeviceExtensionProperties");

					if (enumerate == null || getProperties == null)
						return (VulkanState.Unknown, null);

					uint count = 0;

					if (enumerate(instance, &count, null) != 0 || count == 0)
						return (VulkanState.NoDevice, null);

					var devices = stackalloc nint[(int)Math.Min(count, 16)];
					count = Math.Min(count, 16);

					if (enumerate(instance, &count, devices) < 0)
						return (VulkanState.NoDevice, null);

					VulkanState best = VulkanState.NoDevice;
					string? name = null;

					for (var i = 0; i < count; i++)
					{
						var props = stackalloc byte[1024];
						new Span<byte>(props, 1024).Clear();
						getProperties(devices[i], props);

						// VkPhysicalDeviceProperties: deviceType at 16 (4 = CPU), deviceName (256 bytes) at 20
						var type = *(int*)(props + 16);
						var deviceName = Marshal.PtrToStringUTF8((nint)(props + 20)) ?? "?";

						// the game needs the swapchain extension to show anything
						if (enumerateExtensions != null && !HasSwapchain(enumerateExtensions, devices[i]))
							continue;

						if (type == 4)
						{
							if (best == VulkanState.NoDevice)
							{
								best = VulkanState.SoftwareOnly;
								name = deviceName;
							}

							continue;
						}

						return (VulkanState.Ok, deviceName);
					}

					return (best, name);
				}
				finally
				{
					var destroy = (delegate* unmanaged<nint, nint, void>)GetProc(vkGetInstanceProcAddr, instance, "vkDestroyInstance");

					if (destroy != null)
						destroy(instance, 0);
				}
			}
			finally
			{
				NativeLibrary.Free(loader);
			}
		}
		catch (Exception)
		{
			// the check is a help and never in the way: a probe that falls over says nothing against the machine
			return (VulkanState.Unknown, null);
		}
	}

	private static unsafe nint GetProc(delegate* unmanaged<nint, byte*, nint> getProc, nint instance, string name)
	{
		var bytes = Encoding.ASCII.GetBytes(name + "\0");

		fixed (byte* p = bytes)
			return getProc(instance, p);
	}

	private static unsafe bool HasSwapchain(delegate* unmanaged<nint, byte*, uint*, byte*, int> enumerate, nint device)
	{
		uint count = 0;

		if (enumerate(device, null, &count, null) < 0 || count == 0)
			return false;

		var size = checked((int)count * 260);
		var buffer = new byte[size];

		fixed (byte* p = buffer)
		{
			if (enumerate(device, null, &count, p) < 0)
				return false;

			for (var i = 0; i < count; i++)
			{
				if (Marshal.PtrToStringUTF8((nint)(p + i * 260)) == "VK_KHR_swapchain")
					return true;
			}
		}

		return false;
	}
}

/// <summary>Which package manager a Linux uses, and the packages the game's libraries come in there.</summary>
public sealed record InstallPlan(string Manager, string Command)
{
	/// <summary>
	/// The plan for a system described by the ID and ID_LIKE lines of /etc/os-release, or null when it is none of the known ones.
	/// `driver` adds the Vulkan driver for AMD and Intel cards (NVIDIA's own driver is not touched).
	/// </summary>
	public static InstallPlan? For(string osRelease, SystemReport report)
	{
		var ids = Ids(osRelease);
		var libraries = report.MissingLibraries;
		var wantLoader = libraries.Contains("libvulkan.so.1");
		var wantGlfw = libraries.Contains("libglfw.so.3");
		var wantDriver = report.Vulkan is VulkanState.NoDevice or VulkanState.SoftwareOnly || wantLoader;

		if (!wantLoader && !wantGlfw && !wantDriver)
			return null;

		List<string> packages;
		string template;
		string manager;

		if (ids.Overlaps(["debian", "ubuntu", "linuxmint", "pop", "raspbian"]))
		{
			manager = "apt";
			packages = [];
			if (wantGlfw) packages.Add("libglfw3");
			if (wantLoader) packages.Add("libvulkan1");
			if (wantDriver) packages.Add("mesa-vulkan-drivers");
			template = "apt-get update && apt-get install -y {0}";
		}
		else if (ids.Overlaps(["fedora", "rhel", "centos", "nobara"]))
		{
			manager = "dnf";
			packages = [];
			if (wantGlfw) packages.Add("glfw");
			if (wantLoader) packages.Add("vulkan-loader");
			if (wantDriver) packages.Add("mesa-vulkan-drivers");
			template = "dnf install -y {0}";
		}
		else if (ids.Overlaps(["arch", "manjaro", "endeavouros", "cachyos", "garuda"]))
		{
			manager = "pacman";
			packages = [];
			if (wantGlfw) packages.Add("glfw");
			if (wantLoader) packages.Add("vulkan-icd-loader");
			if (wantDriver) packages.AddRange(["vulkan-radeon", "vulkan-intel"]);
			template = "pacman -S --needed --noconfirm {0}";
		}
		else if (ids.Overlaps(["opensuse", "opensuse-leap", "opensuse-tumbleweed", "suse", "sles"]))
		{
			manager = "zypper";
			packages = [];
			if (wantGlfw) packages.Add("libglfw3");
			if (wantLoader) packages.Add("libvulkan1");
			if (wantDriver) packages.AddRange(["libvulkan_radeon", "libvulkan_intel"]);
			template = "zypper --non-interactive install {0}";
		}
		else
		{
			return null;
		}

		return new InstallPlan(manager, string.Format(template, string.Join(' ', packages)));
	}

	/// <summary>The ID and the ID_LIKE words of an os-release file, lower case.</summary>
	public static HashSet<string> Ids(string osRelease)
	{
		var ids = new HashSet<string>();

		foreach (var line in osRelease.Split('\n'))
		{
			var trimmed = line.Trim();

			if (!trimmed.StartsWith("ID=") && !trimmed.StartsWith("ID_LIKE="))
				continue;

			var value = trimmed[(trimmed.IndexOf('=') + 1)..].Trim('"', '\'').ToLowerInvariant();

			foreach (var word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
				ids.Add(word);
		}

		return ids;
	}

	/// <summary>Whether a graphical way of asking for the administrator's password is there.</summary>
	public static bool CanAskForPassword => OperatingSystem.IsLinux() && (File.Exists("/usr/bin/pkexec") || File.Exists("/bin/pkexec"));

	/// <summary>Runs the plan through pkexec (the desktop's own password dialog). Returns the program's exit code and what it printed.</summary>
	public async Task<(int Code, string Output)> RunAsync(CancellationToken ct = default)
	{
		var start = new ProcessStartInfo("pkexec") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		start.ArgumentList.Add("sh");
		start.ArgumentList.Add("-c");
		start.ArgumentList.Add(Command);

		using var process = Process.Start(start) ?? throw new LauncherException("Der Installationsbefehl konnte nicht gestartet werden.");
		var output = new StringBuilder();

		process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
		process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		await process.WaitForExitAsync(ct);

		return (process.ExitCode, output.ToString());
	}
}
