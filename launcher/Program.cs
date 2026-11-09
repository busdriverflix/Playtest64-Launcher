using Avalonia;

namespace Playtest64.Launcher;

/*
	The Playtest64 launcher: signs a tester in, lists the builds of the game, installs the one they pick and
	starts it; in the background it looks now and then for a new build and says so.

	  Playtest64Launcher                the window
	  Playtest64Launcher --background   the background check with a tray icon, no window: for the autostart
	  Playtest64Launcher --system       what the machine lacks for the game (libraries, Vulkan), written to the console
	  Playtest64Launcher --check        one check, written to the console, then it stops (for tests and timers)
*/
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        SelfUpdate.CleanUp();

        if (args.Contains("--system"))
            return SystemOnce();

        if (args.Contains("--check"))
            return CheckOnce();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static int SystemOnce()
    {
        var report = SystemCheck.Run();

        Console.WriteLine($"Vulkan: {report.Vulkan}{(report.Device != null ? " (" + report.Device + ")" : "")}");
        Console.WriteLine("Missing libraries: " + (report.MissingLibraries.Count == 0 ? "none" : string.Join(", ", report.MissingLibraries)));

        return report.HasProblem ? 1 : 0;
    }

    private static int CheckOnce()
    {
        var context = new LauncherContext();
        var result = UpdateCheck.RunAsync(context.Settings, context.Api, context.Store).GetAwaiter().GetResult();

        var text = result.Kind switch
        {
            UpdateKind.NewBuild => $"NEW {result.Build!.Version}",
            UpdateKind.NewLauncher => $"LAUNCHER {result.Launcher!.Version}",
            UpdateKind.NewBoth => $"NEW {result.Build!.Version} LAUNCHER {result.Launcher!.Version}",
            _ => result.Kind.ToString().ToUpperInvariant(),
        };

        Console.WriteLine(text);

        return 0;
    }
}
