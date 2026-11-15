using STS2_DamageCharts;
using Xunit;

public class ShareLayoutTests
{
    // The game's rows at 1920x1080 (game_over_screen.tscn): Continue, then Main Menu at its resting spot.
    private static readonly Box Continue = new(830, 910, 260, 58);
    private static readonly Box MainMenu = new(818, 910, 260, 58);
    private static readonly Box None = default;

    [Fact]
    public void SitsRightOfTheRowCentredOnIt()
    {
        var b = ShareLayout.Place(MainMenu, None, None, 1920, 1080, 1f);
        Assert.Equal(new Box(1102, 909, 220, 60), b);
    }

    [Fact]
    public void ScalesWithTheUi()
    {
        var row = new Box(1245, 1365, 390, 87);   // Continue at 2880x1620
        var b = ShareLayout.Place(row, None, None, 2880, 1620, 1.5f);
        Assert.Equal(1245 + 390 + 36, b.X);
        Assert.Equal(330, b.W);
        Assert.Equal(90, b.H);
        Assert.Equal(row.CenterY, b.CenterY);
    }

    [Fact]
    public void GoesLeftWhenContentIsOnTheRight()
    {
        var content = new Box(1100, 600, 400, 400);
        var b = ShareLayout.Place(MainMenu, content, None, 1920, 1080, 1f);
        Assert.Equal(818 - 24 - 220, b.X);
        Assert.False(b.Intersects(content));
    }

    [Fact]
    public void GoesBelowWhenBothSidesAreTaken()
    {
        var b = ShareLayout.Place(MainMenu, new Box(1090, 880, 300, 120), new Box(500, 880, 310, 120), 1920, 1080, 1f);
        Assert.Equal(new Box(838, 980, 220, 60), b);
        Assert.False(b.Intersects(MainMenu));
    }

    [Fact]
    public void ClampsToTheViewportWhenNothingFits()
    {
        var everywhere = new Box(0, 0, 1920, 1080);
        var b = ShareLayout.Place(MainMenu, everywhere, None, 1920, 1080, 1f);
        Assert.Equal(new Box(1102, 909, 220, 60), b);   // the right-hand spot, already inside
    }

    [Fact]
    public void NarrowViewportPicksASpotThatFits()
    {
        var row = new Box(520, 910, 260, 58);   // bottom-centre of 1300 wide
        var b = ShareLayout.Place(row, None, None, 1300, 1080, 1f);
        Assert.True(b.X >= 24 && b.Right <= 1300 - 24);
        Assert.False(b.Intersects(row));
    }

    [Fact]
    public void NoRowFallsBackToContinuesSpot()
    {
        var fallback = ShareLayout.FallbackRow(1920, 1080, 1f);
        Assert.Equal(Continue, fallback);
        Assert.Equal(ShareLayout.Place(Continue, None, None, 1920, 1080, 1f), ShareLayout.Place(None, None, None, 1920, 1080, 1f));
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1080)]
    [InlineData(3840, 2160)]
    public void NeverCoversTheRow(float w, float h)
    {
        float sc = System.Math.Clamp(h / 1080f, 0.75f, 2f);
        var row = ShareLayout.FallbackRow(w, h, sc);
        var b = ShareLayout.Place(row, None, None, w, h, sc);
        Assert.False(b.Intersects(row));
        Assert.True(b.X >= 0 && b.Y >= 0 && b.Right <= w && b.Bottom <= h);
    }

    [Fact]
    public void BadScaleIsTreatedAsOne()
    {
        Assert.Equal(ShareLayout.Place(MainMenu, None, None, 1920, 1080, 1f), ShareLayout.Place(MainMenu, None, None, 1920, 1080, float.NaN));
        Assert.Equal(ShareLayout.Place(MainMenu, None, None, 1920, 1080, 1f), ShareLayout.Place(MainMenu, None, None, 1920, 1080, 0f));
    }

    [Fact]
    public void BoxesThatOnlyTouchDoNotIntersect()
    {
        Assert.False(new Box(0, 0, 10, 10).Intersects(new Box(10, 0, 10, 10)));
        Assert.True(new Box(0, 0, 10, 10).Intersects(new Box(9, 9, 10, 10)));
        Assert.False(new Box(0, 0, 10, 10).Intersects(None));
        Assert.Equal(Continue, None.Union(Continue));
        Assert.Equal(new Box(818, 910, 272, 58), MainMenu.Union(Continue));
        Assert.True(new Box(float.NaN, 0, 10, 10).IsEmpty);
    }

    [Fact]
    public void GlowPulsesThenStops()
    {
        Assert.True(ShareLayout.Glow(0) > 0.3f);
        Assert.True(ShareLayout.Glow(500) > 0.9f);            // the first peak
        Assert.Equal(0f, ShareLayout.Glow(3000));
        Assert.Equal(0f, ShareLayout.Glow(60_000));
        Assert.Equal(0f, ShareLayout.Glow(-1));
        Assert.Equal(0f, ShareLayout.Glow(double.NaN));
        Assert.True(ShareLayout.Glow(2999) < 0.01f);          // fades to nothing, no pop at the end
        for (double t = 0; t < 3000; t += 7)
        {
            float g = ShareLayout.Glow(t);
            Assert.InRange(g, 0f, 1f);
        }
    }
}
