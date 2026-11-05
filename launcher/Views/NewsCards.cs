using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Playtest64.Launcher.Views;

/// <summary>
/// A news post as a card: the title and date, the text in paragraphs and bullet lists, a place for its
/// pictures (filled in as they arrive), and, for a post that announces a build, a button that picks it.
/// The colours are the theme's, looked up by name so that a change of look reaches the cards already shown.
/// </summary>
public static class NewsCards
{
    public sealed record Card(Control Control, Panel Pictures);

    public static Card Build(NewsItem item, Action<string>? pickBuild)
    {
        var body = new StackPanel { Spacing = 8 };

        var title = new TextBlock { Text = item.Title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Themed(title, TextBlock.ForegroundProperty, "ThemeText");

        var date = new TextBlock { Text = FormatDate(item.Date), FontSize = 12 };
        Themed(date, TextBlock.ForegroundProperty, "ThemeDim");

        body.Children.Add(date);
        body.Children.Add(title);

        foreach (var block in TextBlocks.Parse(item.Content))
        {
            switch (block)
            {
                case TextBlockData.Paragraph paragraph:
                    var text = new TextBlock { Text = paragraph.Text, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
                    Themed(text, TextBlock.ForegroundProperty, "ThemeMuted");
                    body.Children.Add(text);
                    break;

                case TextBlockData.BulletList list:
                    var items = new StackPanel { Spacing = 3 };

                    foreach (var entry in list.Items)
                    {
                        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                        var bullet = new TextBlock { Text = "•", Margin = new Thickness(4, 0, 8, 0) };
                        var line = new TextBlock { Text = entry, TextWrapping = TextWrapping.Wrap };

                        Themed(bullet, TextBlock.ForegroundProperty, "ThemePrimary");
                        Themed(line, TextBlock.ForegroundProperty, "ThemeMuted");
                        Grid.SetColumn(line, 1);
                        row.Children.Add(bullet);
                        row.Children.Add(line);
                        items.Children.Add(row);
                    }

                    body.Children.Add(items);
                    break;
            }
        }

        var pictures = new StackPanel { Spacing = 8 };
        body.Children.Add(pictures);

        if (pickBuild != null && !string.IsNullOrEmpty(item.BuildVersion))
        {
            var button = new Button { Content = $"Build {item.BuildVersion} auswählen", HorizontalAlignment = HorizontalAlignment.Left };
            button.Click += (_, _) => pickBuild(item.BuildVersion);
            body.Children.Add(button);
        }

        var card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(18, 14), Child = body };

        Themed(card, Border.BackgroundProperty, "ThemeSurface");
        Themed(card, Border.BorderBrushProperty, "ThemeBorder");

        return new Card(card, pictures);
    }

    // "2026-10-30" as the date is written here: 30.10.2026
    private static string FormatDate(string date)
        => DateTime.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture)
            : date;

    private static void Themed(Control control, AvaloniaProperty property, string resource)
        => control.Bind(property, control.GetResourceObservable(resource));
}
