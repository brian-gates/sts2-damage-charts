using Xunit;
using STS2_DamageCharts.Charts;

public class LogPaletteTests
{
    [Theory]
    [InlineData(LogPalette.Dealt)]
    [InlineData(LogPalette.Taken)]
    [InlineData(LogPalette.Saved)]
    [InlineData(LogPalette.Healed)]
    public void AmountColorsReach45To1OnEveryPanel(string color)
    {
        foreach (var bg in new[] { LogPalette.CardPanel, LogPalette.Track, LogPalette.SummaryPanel, LogPalette.TakenTile })
            Assert.True(LogPalette.Contrast(color, bg) >= 4.5, $"{color} on {bg}: {LogPalette.Contrast(color, bg):0.00}");
    }

    [Fact]
    public void ContrastMatchesWcag()
    {
        Assert.Equal(21.0, LogPalette.Contrast("ffffff", "000000"), 2);
        Assert.Equal(1.0, LogPalette.Contrast("12222a", "12222a"), 6);
        Assert.Equal(LogPalette.Contrast("ff8a80", "12222a"), LogPalette.Contrast("12222a", "ff8a80"), 9);
        Assert.True(LogPalette.Contrast("ff5555", "1d2f36") < 4.5);   // the old taken red failed on the track tint
    }
}
