using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Playtest64.Launcher;

/// <summary>
/// The builds on disk. One folder per version under builds/, with installed.txt in it once the build is
/// complete: a build that was cut off halfway has no marker and is not an installed one. The marker holds
/// the name of the folder the zip unpacked its game into.
/// </summary>
public sealed partial class BuildStore(string dataDir, LauncherApi api)
{
    private const string Marker = "installed.txt";

    public bool IsInstalled(BuildInfo build) => File.Exists(Path.Combine(FolderOf(build), Marker));

    public async Task InstallAsync(BuildInfo build, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var downloads = Path.Combine(dataDir, "downloads");
        Directory.CreateDirectory(downloads);

        // the name is the server's; only its last part is used, so it can never point somewhere else
        var zip = Path.Combine(downloads, Path.GetFileName(build.FileName.Replace('\\', '/')));
        var part = zip + ".part";

        try
        {
            await api.DownloadAsync(build, part, progress, ct);
            File.Move(part, zip, overwrite: true);
        }
        catch
        {
            File.Delete(part);
            throw;
        }

        var folder = FolderOf(build);
        string top;

        try
        {
            Directory.CreateDirectory(folder);

            using (var archive = ZipFile.OpenRead(zip))
            {
                top = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).FirstOrDefault(n => n.Contains('/'))?.Split('/')[0] ?? "";
            }

            // ExtractToDirectory refuses a name that would land outside the folder
            ZipFile.ExtractToDirectory(zip, folder, overwriteFiles: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            File.Delete(zip);
            throw new LauncherException("Der Build ist beschädigt oder lässt sich nicht entpacken.", ex);
        }

        MakeExecutable(Path.Combine(folder, top));

        // the marker comes last
        File.WriteAllText(Path.Combine(folder, Marker), top + "\n");
        File.Delete(zip);
    }

    public void Play(BuildInfo build)
    {
        var markerPath = Path.Combine(FolderOf(build), Marker);

        if (!File.Exists(markerPath))
            throw new LauncherException("Dieser Build ist nicht installiert.");

        var folder = Path.Combine(FolderOf(build), File.ReadAllText(markerPath).Trim());
        var program = Path.Combine(folder, Platform.GameProgram);

        if (!File.Exists(program))
            throw new LauncherException($"Im Build fehlt das Programm \"{Platform.GameProgram}\".");

        try
        {
            using var process = Process.Start(new ProcessStartInfo(program) { WorkingDirectory = folder, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            throw new LauncherException("Das Spiel lässt sich nicht starten.", ex);
        }
    }

    // A version as a folder name: nothing that could climb out of the builds folder
    private string FolderOf(BuildInfo build)
    {
        var name = UnsafeName().Replace(build.Version, "_");

        return Path.Combine(dataDir, "builds", name.Length > 0 ? name : $"build-{build.BuildId}");
    }

    // The game's own programs must be runnable whatever the zip said about permissions
    private static void MakeExecutable(string folder)
    {
        if (OperatingSystem.IsWindows())
            return;

        foreach (var name in new[] { "game", "game.bin" })
        {
            var path = Path.Combine(folder, name);

            if (File.Exists(path))
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeName();
}
