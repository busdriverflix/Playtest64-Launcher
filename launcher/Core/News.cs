using System.Text.RegularExpressions;

namespace Playtest64.Launcher;

public sealed record NewsPicture(int Id, string Caption);

/// <summary>A news post as the launcher shows it. A post announcing a build says which one (by its version).</summary>
public sealed record NewsItem(int Id, string Title, string Content, string Date, IReadOnlyList<NewsPicture> Pictures, string? BuildVersion);

/// <summary>The news and the ticket that lets the launcher fetch their pictures.</summary>
public sealed record NewsFeed(IReadOnlyList<NewsItem> Items, string MediaTicket);

public abstract record TextBlockData
{
    public sealed record Paragraph(string Text) : TextBlockData;

    public sealed record BulletList(IReadOnlyList<string> Items) : TextBlockData;
}

/// <summary>
/// A post's text as the blocks it is made of, the way the website shows it: line breaks are kept, and a run of
/// lines that each start with "- " or "* " is a list. Anything else is a paragraph. The text is only ever
/// shown as text, so nothing typed in a post can do anything but show up.
/// </summary>
public static partial class TextBlocks
{
    public static IReadOnlyList<TextBlockData> Parse(string text)
    {
        var blocks = new List<TextBlockData>();
        var paragraph = new List<string>();
        List<string>? list = null;

        void EndParagraph()
        {
            if (paragraph.Count > 0)
                blocks.Add(new TextBlockData.Paragraph(string.Join("\n", paragraph)));

            paragraph.Clear();
        }

        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var bullet = Bullet().Match(line);

            if (bullet.Success)
            {
                EndParagraph();

                if (list == null)
                {
                    list = [];
                    blocks.Add(new TextBlockData.BulletList(list));
                }

                list.Add(bullet.Groups[1].Value);
            }
            else
            {
                // anything that is not a bullet ends the list; a blank line ends the paragraph as well
                list = null;

                if (line.Trim().Length == 0)
                    EndParagraph();
                else
                    paragraph.Add(line.Trim());
            }
        }

        EndParagraph();

        return blocks;
    }

    [GeneratedRegex(@"^\s*[-*]\s+(.*\S)\s*$")]
    private static partial Regex Bullet();
}
