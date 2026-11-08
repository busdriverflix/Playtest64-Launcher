using System.Diagnostics;
using System.Security.Cryptography;

namespace Playtest64.Launcher;

/// <summary>The launcher file the server offers for this system.</summary>
public sealed record LauncherRelease(int Id, string Version, string FileName, long Size, string Sha256);

/// <summary>
/// The launcher replacing itself. The new file is downloaded beside the running one, checked against the
/// SHA-256 the server gave, and only then swapped in: the old file is renamed (a running program may be renamed
/// on both systems, deleting it is the part Windows refuses) and removed by the next start.
/// </summary>
public static class SelfUpdate
{
	private const string OldSuffix = ".old";
	private const string NewSuffix = ".new";

	/// <summary>The version of this program, as the project file says it.</summary>
	public static Version Current
	{
		get
		{
			var v = typeof(SelfUpdate).Assembly.GetName().Version ?? new Version(0, 0, 0);

			return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
		}
	}

	public static string CurrentText => Current.ToString();

	/// <summary>Whether the server's version is higher than this program's. A version that cannot be read is never newer.</summary>
	public static bool IsNewer(string server)
	{
		if (!Version.TryParse(server.Trim().TrimStart('v', 'V'), out var other))
			return false;

		return new Version(other.Major, other.Minor, Math.Max(0, other.Build)) > Current;
	}

	/// <summary>The file of the running program, or null where it cannot be told (then there is no updating).</summary>
	public static string? OwnPath => Environment.ProcessPath is { Length: > 0 } p && !Path.GetFileNameWithoutExtension(p).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? p : null;

	/// <summary>Removes what the last update left behind. Never throws: a file still in use is tried again next time.</summary>
	public static void CleanUp()
	{
		try
		{
			if (OwnPath is { } own)
			{
				File.Delete(own + OldSuffix);
				File.Delete(own + NewSuffix);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	/// <summary>Downloads the release, checks it and puts it in the place of `target`. The program keeps running; <see cref="Restart"/> starts the new one.</summary>
	public static async Task ApplyAsync(LauncherApi api, LauncherRelease release, string target, IProgress<double>? progress = null, CancellationToken ct = default)
	{
		var incoming = target + NewSuffix;
		var old = target + OldSuffix;

		try
		{
			await api.DownloadLauncherAsync(release, incoming, progress, ct);

			string hash;

			await using (var read = File.OpenRead(incoming))
				hash = Convert.ToHexString(await SHA256.HashDataAsync(read, ct)).ToLowerInvariant();

			if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
				throw new LauncherException("Das heruntergeladene Update ist beschädigt. Bitte versuche es noch einmal.");

			if (!OperatingSystem.IsWindows())
				File.SetUnixFileMode(incoming, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

			File.Delete(old);
			File.Move(target, old);

			try
			{
				File.Move(incoming, target);
			}
			catch
			{
				// the swap did not finish: put the working file back
				File.Move(old, target);
				throw;
			}
		}
		catch (UnauthorizedAccessException ex)
		{
			throw new LauncherException("Der Launcher darf sich hier nicht ersetzen (kein Schreibzugriff auf seinen Ordner).", ex);
		}
		catch (IOException ex)
		{
			throw new LauncherException("Das Update konnte nicht eingespielt werden: " + ex.Message, ex);
		}
		finally
		{
			try
			{
				File.Delete(incoming);
			}
			catch (IOException)
			{
			}
		}
	}

	/// <summary>Starts the program at `path` with this program's arguments.</summary>
	public static void Restart(string path)
	{
		var start = new ProcessStartInfo(path) { UseShellExecute = false };

		foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
			start.ArgumentList.Add(arg);

		Process.Start(start);
	}
}
