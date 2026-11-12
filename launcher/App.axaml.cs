using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Playtest64.Launcher.Views;

namespace Playtest64.Launcher;

public sealed class App : Application
{
    private const int CheckEveryHours = 6;					// while the launcher window is open
    private const int FirstCheckAfterSeconds = 20;			// the network may not be up yet when the login starts it
    private const int RetryAfterMinutes = 15;				// and again sooner when there was none
    private static readonly int[] LoginRetryMinutes = { 1, 3, 10 };	// the check at login: a few more tries when there is no network yet, then it gives up

    private readonly LauncherContext _context = new();
    private MainWindow? _window;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        ApplyTheme();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.Args?.Contains("--background") == true)
            {
                // The check at login: no window, no tray icon. It looks once, says so if there is news, and ends.
                // Only when the person opens the launcher from the message does the process stay, as the launcher.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _ = CheckAtLoginAsync(desktop);
            }
            else
            {
                _window = new MainWindow(_context);
                desktop.MainWindow = _window;

                // Only while the launcher is open does it look again and again
                _ = WatchAsync();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Puts the look of the day (or the plain one, when the person turned the season's look off) into the
    /// resources the windows read by name. Safe to call again: windows that are open follow it.
    /// </summary>
    public void ApplyTheme()
    {
        var palette = Themes.Current(_context.Settings.SeasonalLook);

        Brush("ThemeBg", palette.Bg);
        Brush("ThemeSurface", palette.Surface);
        Brush("ThemeSurfaceHover", palette.SurfaceHover);
        Brush("ThemeBorder", palette.Border);
        Brush("ThemePrimary", palette.Primary);
        Brush("ThemeText", palette.Text);
        Brush("ThemeMuted", palette.Muted);
        Brush("ThemeDim", palette.Dim);

        // The Fluent controls take their accent from these: the main button, the progress bar, the ticked boxes
        Resources["SystemAccentColor"] = Color.Parse(palette.Primary);
        Brush("AccentButtonBackground", palette.Primary);
        Brush("AccentButtonBackgroundPointerOver", palette.PrimaryHover);
        Brush("AccentButtonBackgroundPressed", palette.PrimaryHover);
        Brush("AccentButtonForeground", palette.OnPrimary);
        Brush("AccentButtonForegroundPointerOver", palette.OnPrimary);
        Brush("AccentButtonForegroundPressed", palette.OnPrimary);
    }

    private void Brush(string key, string colour) => Resources[key] = new Avalonia.Media.SolidColorBrush(Color.Parse(colour));

    private void OpenWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_window is { IsVisible: true })
        {
            _window.Activate();
            return;
        }

        _window = new MainWindow(_context);
        desktop.MainWindow ??= _window;

        // The check at login has no window of its own: when the one it was asked for is closed, it is over
        if (desktop.ShutdownMode == ShutdownMode.OnExplicitShutdown)
            _window.Closed += (_, _) => desktop.Shutdown();

        _window.Show();
    }

    // The check at login: once, with a few more tries when the network is not up yet, then the process ends
    private async Task CheckAtLoginAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Task.Delay(TimeSpan.FromSeconds(FirstCheckAfterSeconds));

        for (var attempt = 0; ; attempt++)
        {
            var told = await CheckOnceAsync(true);

            if (told.Toast != null)
            {
                // the message is up for some seconds; a click on it opens the launcher and the process stays
                await told.Toast;
                break;
            }

            if (told.Offline && attempt < LoginRetryMinutes.Length)
            {
                await Task.Delay(TimeSpan.FromMinutes(LoginRetryMinutes[attempt]));
                continue;
            }

            break;
        }

        if (_window is not { IsVisible: true })
            desktop.Shutdown();
    }

    // While the launcher is open: now and then, until it is closed. A new build is told once; a sign-in that ran out is told once too.
    private async Task WatchAsync()
    {
        var signedOutTold = false;

        await Task.Delay(TimeSpan.FromSeconds(FirstCheckAfterSeconds));

        while (true)
        {
            var told = await CheckOnceAsync(!signedOutTold);

            if (told.SignedOut)
                signedOutTold = true;

            var delay = told.Offline ? TimeSpan.FromMinutes(RetryAfterMinutes) : TimeSpan.FromHours(CheckEveryHours);

            await Task.Delay(delay);
        }
    }

    private sealed record Told(bool Offline, Task? Toast, bool SignedOut = false);

    // One look at the server; `tellSignedOut`: whether a sign-in that ran out is to be told (once is enough)
    private async Task<Told> CheckOnceAsync(bool tellSignedOut)
    {
        try
        {
            var result = await UpdateCheck.RunAsync(_context.Settings, _context.Api, _context.Store);

            switch (result.Kind)
            {
                case UpdateKind.NewBuild:
                    return new Told(false, ShowToast("Neuer Playtest-Build", $"Version {result.Build!.Version} ist da. Klicke hier, um den Launcher zu öffnen."));

                case UpdateKind.NewLauncher:
                    return new Told(false, ShowToast("Launcher-Update", $"Version {result.Launcher!.Version} ist da. Klicke hier, um den Launcher zu öffnen und zu aktualisieren."));

                case UpdateKind.NewBoth:
                    return new Told(false, ShowToast("Updates für Playtest64", $"Neuer Build {result.Build!.Version} und neuer Launcher {result.Launcher!.Version}. Klicke hier, um den Launcher zu öffnen."));

                case UpdateKind.SignedOut:
                    return new Told(false, tellSignedOut ? ShowToast("Playtest64", "Bitte melde dich im Launcher neu an, damit nach Updates gesucht werden kann.") : null, true);

                case UpdateKind.Offline:
                    return new Told(true, null);
            }
        }
        catch (Exception)
        {
            // the check must never end the process: whatever it was, the next round tries again
            return new Told(true, null);
        }

        return new Told(false, null);
    }

    // Shows the message; the task is done when it has gone away again
    private Task ShowToast(string title, string text)
    {
        var gone = new TaskCompletionSource();

        Dispatcher.UIThread.Post(() =>
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var toast = new ToastWindow(title, text, () => OpenWindow(desktop));
                toast.Closed += (_, _) => gone.TrySetResult();
                toast.Show();
            }
            else
                gone.TrySetResult();
        });

        return gone.Task;
    }
}
