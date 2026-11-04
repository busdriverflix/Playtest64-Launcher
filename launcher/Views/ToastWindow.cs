using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Playtest64.Launcher.Views;

/// <summary>
/// The message in the corner of the screen. The same on every system and with nothing to install: a small
/// window without a frame, over everything, that goes away by itself, and opens the launcher when it is clicked.
/// </summary>
public sealed class ToastWindow : Window
{
    private const int Width_ = 360;
    private const int Height_ = 96;
    private const int Margin_ = 16;
    private const int ShownSeconds = 12;

    public ToastWindow(string title, string text, Action clicked)
    {
        Width = Width_;
        Height = Height_;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        Background = new SolidColorBrush(Color.Parse("#1E1E1E"));

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#4C8DFF")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 12),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15 },
                    new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#B8B8B8")) },
                },
            },
        };

        PointerPressed += (_, _) =>
        {
            clicked();
            Close();
        };

        Opened += (_, _) =>
        {
            // the bottom right corner of the main screen, above the taskbar
            if (Screens.Primary is { } screen)
            {
                var scale = screen.Scaling;
                Position = new PixelPoint(
                    screen.WorkingArea.Right - (int)((Width_ + Margin_) * scale),
                    screen.WorkingArea.Bottom - (int)((Height_ + Margin_) * scale));
            }
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ShownSeconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Close();
        };
        Opened += (_, _) => timer.Start();
    }
}
