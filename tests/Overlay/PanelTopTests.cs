using Xunit;
using STS2_DamageCharts.Overlay;

public class PanelTopTests
{
    [Fact]
    public void RelicRowLowerThanFallback_UsesRelicRowBottom()
    {
        // 1080p fallback is 9% of 1080 = 97.2; a relic row ending at 180 wins.
        Assert.Equal(180f, PanelTop.Inset(1080f, 180f));
    }

    [Fact]
    public void RelicRowAboveFallback_UsesFallback()
    {
        Assert.Equal(1080f * 0.09f, PanelTop.Inset(1080f, 60f), 3);
    }

    [Fact]
    public void NoRelicRow_UsesFallback()
    {
        Assert.Equal(1080f * 0.09f, PanelTop.Inset(1080f, null), 3);
        Assert.Equal(48f, PanelTop.Inset(300f, null));   // the 48 px floor on small windows
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void NonFiniteRelicBottom_UsesFallback(float bottom)
    {
        Assert.Equal(1080f * 0.09f, PanelTop.Inset(1080f, bottom), 3);
    }

    [Theory]
    [InlineData(540f)]    // the HFlowContainer's own rect: half the screen
    [InlineData(433f)]    // just past 40%
    public void RelicBottomDeeperThan40Percent_UsesFallback(float bottom)
    {
        Assert.Equal(1080f * 0.09f, PanelTop.Inset(1080f, bottom), 3);
    }

    [Fact]
    public void RelicBottomAt40Percent_IsStillUsed()
    {
        Assert.Equal(432f, PanelTop.Inset(1080f, 432f), 3);
    }
}
