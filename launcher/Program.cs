using Avalonia;

namespace Playtest64.Launcher;

/*
	The Playtest64 launcher: signs a tester in, lists the builds of the game, installs the one they pick and
	starts it; in the background it looks now and then for a new build and says so.

	  Playtest64Launcher                the window
	  Playtest64Launcher --background   the background check with a tray icon, no window: for the autostart
	  Playtest64Launcher --check        one check, written to the console, then it stops (for tests and timers)
*/
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--check"))
            return CheckOnce();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static int CheckOnce()
    {
        var context = new LauncherContext();
        var result = UpdateCheck.RunAsync(context.Settings, context.Api, context.Store).GetAwaiter().GetResult();

        Console.WriteLine(result.Kind == UpdateKind.NewBuild ? $"NEW {result.Build!.Version}" : result.Kind.ToString().ToUpperInvariant());

        return 0;
    }
}
