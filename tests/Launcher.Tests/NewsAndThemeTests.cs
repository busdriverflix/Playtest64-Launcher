using Xunit;

namespace Playtest64.Launcher.Tests;

public class NewsAndThemeTests
{
    [Fact]
    public async Task The_news_come_with_their_pictures_and_the_build_they_announce()
    {
        using var rig = new Rig();

        var feed = await rig.Api.GetNewsAsync();

        Assert.Equal(["Update: Animationen", "Ankündigung"], feed.Items.Select(i => i.Title));
        Assert.Equal("0.2.1", feed.Items[0].BuildVersion);
        Assert.Null(feed.Items[1].BuildVersion);
        Assert.Single(feed.Items[0].Pictures);
        Assert.Equal("ticket-1", feed.MediaTicket);

        var picture = await rig.Api.GetPictureAsync(feed.Items[0].Pictures[0].Id, feed.MediaTicket);
        Assert.Equal(4, picture.Length);
    }

    [Fact]
    public async Task The_news_need_a_sign_in_and_a_dead_token_signs_the_person_out()
    {
        using var rig = new Rig();
        rig.Settings.Token = "stale";

        await Assert.ThrowsAsync<SignedOutException>(() => rig.Api.GetNewsAsync());

        Assert.False(rig.Settings.SignedIn);
    }

    [Fact]
    public void Lines_with_a_dash_are_a_list_and_line_breaks_are_kept()
    {
        var blocks = TextBlocks.Parse("Neu:\nzweite Zeile\n\n- Skid\n* Seitensalto\n\nViel Spaß!");

        Assert.Equal(3, blocks.Count);
        Assert.Equal("Neu:\nzweite Zeile", ((TextBlockData.Paragraph)blocks[0]).Text);
        Assert.Equal(["Skid", "Seitensalto"], ((TextBlockData.BulletList)blocks[1]).Items);
        Assert.Equal("Viel Spaß!", ((TextBlockData.Paragraph)blocks[2]).Text);
    }

    [Fact]
    public void A_dash_in_the_middle_of_a_line_is_not_a_list()
    {
        var blocks = TextBlocks.Parse("Das ist - ein Satz\n-kein Punkt");

        Assert.Single(blocks);
        Assert.IsType<TextBlockData.Paragraph>(blocks[0]);
    }

    [Theory]
    [InlineData(2026, 10, 1, "Halloween")]
    [InlineData(2026, 11, 7, "Halloween")]
    [InlineData(2026, 11, 8, "Standard")]
    [InlineData(2026, 11, 30, "Standard")]
    [InlineData(2026, 12, 1, "Weihnachten")]
    [InlineData(2026, 12, 26, "Weihnachten")]
    [InlineData(2026, 12, 27, "Silvester")]
    [InlineData(2027, 1, 3, "Silvester")]
    [InlineData(2027, 1, 4, "Standard")]
    [InlineData(2027, 3, 14, "Ostern")]			// two weeks before Easter Sunday (28 March 2027)
    [InlineData(2027, 3, 29, "Ostern")]			// Easter Monday
    [InlineData(2027, 3, 30, "Standard")]
    [InlineData(2027, 6, 15, "Standard")]
    public void The_look_follows_the_same_dates_as_the_website(int year, int month, int day, string expected)
        => Assert.Equal(expected, Themes.For(new DateTime(year, month, day)).Name);

    [Fact]
    public void Easter_Sunday_is_right()
    {
        Assert.Equal(new DateTime(2027, 3, 28), Themes.EasterSunday(2027));
        Assert.Equal(new DateTime(2026, 4, 5), Themes.EasterSunday(2026));
    }

    [Fact]
    public void Switched_off_the_look_is_the_ordinary_one()
    {
        Assert.Same(Themes.Ordinary, Themes.Current(seasonal: false));
    }
}
