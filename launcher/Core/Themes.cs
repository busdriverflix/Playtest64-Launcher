namespace Playtest64.Launcher;

/// <summary>The colours of one look. The names are the website's (themes.css), so the two match.</summary>
public sealed record Palette(string Name, string Bg, string Surface, string SurfaceHover, string Border,
    string Primary, string PrimaryHover, string OnPrimary, string Text, string Muted, string Dim);

/// <summary>
/// The seasonal looks of the website, in the launcher: Halloween, Weihnachten, Silvester and Ostern in the
/// same windows of the year, and the ordinary look the rest of it. See composables/useTheme.ts on the website
/// for where the dates come from; the two have to be kept in step by hand.
/// </summary>
public static class Themes
{
    public static readonly Palette Ordinary = new("Standard",
        "#090a0f", "#12141c", "#1a1d28", "#232736", "#3b82f6", "#2563eb", "#ffffff", "#f1f5f9", "#94a3b8", "#64748b");

    public static readonly Palette Halloween = new("Halloween",
        "#0b0612", "#170d22", "#21122f", "#3a2250", "#ff7a1a", "#e8650a", "#ffffff", "#f6ecff", "#c4aadb", "#8d71a6");

    public static readonly Palette Weihnachten = new("Weihnachten",
        "#07130d", "#0e2217", "#143122", "#25503a", "#e5393d", "#c82b2f", "#ffffff", "#f4fbf6", "#b5d3c0", "#72a085");

    public static readonly Palette Silvester = new("Silvester",
        "#060919", "#0e1330", "#161d45", "#29346a", "#f5c542", "#dca92a", "#1b1400", "#f5f7ff", "#b6bfe6", "#7783b8");

    public static readonly Palette Ostern = new("Ostern",
        "#0a130f", "#13221a", "#1b3125", "#2f5a42", "#f58fb5", "#e4749f", "#2a0b17", "#f5fff8", "#bcdcc8", "#7ba88d");

    /// <summary>The look a day calls for; the ordinary one on an ordinary day.</summary>
    public static Palette For(DateTime date)
    {
        if (date.Month == 10 || (date.Month == 11 && date.Day <= 7))
            return Halloween;

        if (date.Month == 12 && date.Day <= 26)
            return Weihnachten;

        if ((date.Month == 12 && date.Day >= 27) || (date.Month == 1 && date.Day <= 3))
            return Silvester;

        var easter = EasterSunday(date.Year);

        if (date.Date >= easter.AddDays(-14) && date.Date <= easter.AddDays(1))
            return Ostern;

        return Ordinary;
    }

    /// <summary>
    /// The look to show. PLAYTEST_THEME (halloween, weihnachten, silvester, ostern, standard) forces one, the way
    /// the website's ?theme= does; with the season's look switched off it is the ordinary one.
    /// </summary>
    public static Palette Current(bool seasonal)
    {
        var forced = Environment.GetEnvironmentVariable("PLAYTEST_THEME")?.ToLowerInvariant();

        var palette = forced switch
        {
            "halloween" => Halloween,
            "weihnachten" => Weihnachten,
            "silvester" => Silvester,
            "ostern" => Ostern,
            "standard" => Ordinary,
            _ => null,
        };

        if (palette != null)
            return palette;

        return seasonal ? For(DateTime.Now) : Ordinary;
    }

    /// <summary>Easter Sunday of a year, by the Gaussian rule for the Gregorian calendar.</summary>
    public static DateTime EasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = (h + l - 7 * m + 114) % 31 + 1;

        return new DateTime(year, month, day);
    }
}
