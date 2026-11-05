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
    private const int CheckEveryHours = 6;
    private const int FirstCheckAfterSeconds = 20;			// the network may not be up yet when the login starts it
    private const int RetryAfterMinutes = 15;				// and again sooner when there was none

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
                // no window: the process lives until "Beenden" in the tray menu
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                AddTrayIcon(desktop);
                _ = WatchAsync();
            }
            else
            {
                _window = new MainWindow(_context);
                desktop.MainWindow = _window;
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

    private void AddTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var open = new NativeMenuItem("Launcher öffnen");
        open.Click += (_, _) => OpenWindow(desktop);

        var quit = new NativeMenuItem("Beenden");
        quit.Click += (_, _) => desktop.Shutdown();

        TrayIcon.SetIcons(this, new TrayIcons
        {
            new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Playtest64Launcher/Assets/icon.png"))),
                ToolTipText = "Playtest64 Launcher",
                Menu = new NativeMenu { open, quit },
            },
        });
    }

    private void OpenWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_window is { IsVisible: true })
        {
            _window.Activate();
            return;
        }

        _window = new MainWindow(_context);
        desktop.MainWindow ??= _window;
        _window.Show();
    }

    // The background check: now and then, forever. A new build is told once; a sign-in that ran out is told once too.
    private async Task WatchAsync()
    {
        var signedOutTold = false;

        await Task.Delay(TimeSpan.FromSeconds(FirstCheckAfterSeconds));

        while (true)
        {
            var delay = TimeSpan.FromHours(CheckEveryHours);

            try
            {
                var result = await UpdateCheck.RunAsync(_context.Settings, _context.Api, _context.Store);

                switch (result.Kind)
                {
                    case UpdateKind.NewBuild:
                        ShowToast("Neuer Playtest-Build", $"Version {result.Build!.Version} ist da. Klicke hier, um den Launcher zu öffnen.");
                        break;

                    case UpdateKind.SignedOut when !signedOutTold:
                        signedOutTold = true;
                        ShowToast("Playtest64", "Bitte melde dich im Launcher neu an, damit nach Updates gesucht werden kann.");
                        break;

                    case UpdateKind.Offline:
                        delay = TimeSpan.FromMinutes(RetryAfterMinutes);
                        break;
                }
            }
            catch (Exception)
            {
                // the check must never end the process: whatever it was, the next round tries again
                delay = TimeSpan.FromMinutes(RetryAfterMinutes);
            }

            await Task.Delay(delay);
        }
    }

    private void ShowToast(string title, string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                new ToastWindow(title, text, () => OpenWindow(desktop)).Show();
        });
    }
}
