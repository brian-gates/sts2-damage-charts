using STS2_DamageCharts;
using Xunit;

public class ShareLayoutTests
{
    // The game's rows at 1920x1080 (game_over_screen.tscn): Continue, then Main Menu at its resting spot.
    private static readonly Box Continue = new(830, 910, 260, 58);
    private static readonly Box MainMenu = new(818, 910, 260, 58);
    private static readonly Box None = default;

    // A fresh placement (no earlier spot).
    private static Box P(Box row, Box a, Box b, float w, float h, float sc)
    {
        int spot = ShareLayout.NoSpot;
        return ShareLayout.Place(row, a, b, w, h, sc, ref spot);
    }

    [Fact]
    public void SitsRightOfTheRowCentredOnIt()
    {
        var b = P(MainMenu, None, None, 1920, 1080, 1f);
        Assert.Equal(new Box(1102, 909, 220, 60), b);
    }

    [Fact]
    public void ScalesWithTheUi()
    {
        var row = new Box(1245, 1365, 390, 87);   // Continue at 2880x1620
        var b = P(row, None, None, 2880, 1620, 1.5f);
        Assert.Equal(1245 + 390 + 36, b.X);
        Assert.Equal(330, b.W);
        Assert.Equal(90, b.H);
        Assert.Equal(row.CenterY, b.CenterY);
    }

    [Fact]
    public void GoesLeftWhenContentIsOnTheRight()
    {
        var content = new Box(1100, 600, 400, 400);
        var b = P(MainMenu, content, None, 1920, 1080, 1f);
        Assert.Equal(818 - 24 - 220, b.X);
        Assert.False(b.Intersects(content));
    }

    [Fact]
    public void GoesBelowWhenBothSidesAreTaken()
    {
        var b = P(MainMenu, new Box(1090, 880, 300, 120), new Box(500, 880, 310, 120), 1920, 1080, 1f);
        Assert.Equal(new Box(838, 980, 220, 60), b);
        Assert.False(b.Intersects(MainMenu));
    }

    [Fact]
    public void WhenNothingFitsTakesTheLeastOverlapNotTheRow()
    {
        // 600x500: no spot fits (right and left run off the sides, below off the bottom), so each is clamped
        // back over the row; content covers the left. Right overlaps least.
        var row = new Box(170, 400, 260, 58);
        var content = new Box(0, 0, 200, 500);
        int spot = ShareLayout.NoSpot;
        var b = ShareLayout.Place(row, content, None, 600, 500, 1f, ref spot);
        Assert.Equal(ShareLayout.Right, spot);
        Assert.Equal(new Box(356, 399, 220, 60), b);
        float cost = ShareLayout.RowWeight * ShareLayout.Overlap(b, row) + ShareLayout.Overlap(b, content);
        foreach (var c in new[] { new Box(24, 399, 220, 60), new Box(190, 416, 220, 60) })   // left, below (clamped)
            Assert.True(cost < ShareLayout.RowWeight * ShareLayout.Overlap(c, row) + ShareLayout.Overlap(c, content));
    }

    [Fact]
    public void KeepsItsSpotWhileItStillFits()
    {
        int spot = ShareLayout.NoSpot;
        var leftOnly = new Box(1100, 600, 400, 400);   // blocks the right: goes left
        var first = ShareLayout.Place(MainMenu, leftOnly, None, 1920, 1080, 1f, ref spot);
        Assert.Equal(ShareLayout.Left, spot);
        // The content moves away: the right spot fits again, but the button stays left (no flip).
        var again = ShareLayout.Place(MainMenu, None, None, 1920, 1080, 1f, ref spot);
        Assert.Equal(first, again);
        Assert.Equal(ShareLayout.Left, spot);
        // Something now covers the left spot: it moves, once, to the first spot that fits.
        var moved = ShareLayout.Place(MainMenu, new Box(500, 880, 310, 120), None, 1920, 1080, 1f, ref spot);
        Assert.Equal(ShareLayout.Right, spot);
        Assert.Equal(1102, moved.X);
    }

    [Fact]
    public void WhenNothingFitsTiesKeepTheCurrentSpot()
    {
        var everywhere = new Box(0, 0, 1920, 1080);
        int spot = ShareLayout.Below;
        var b = ShareLayout.Place(MainMenu, everywhere, None, 1920, 1080, 1f, ref spot);
        Assert.Equal(ShareLayout.Below, spot);   // each spot overlaps the content by its full area
        Assert.Equal(new Box(838, 980, 220, 60), b);
    }

    [Fact]
    public void PanelSitsAboveTheButtonOrAboveTheRow()
    {
        var btn = new Box(1102, 909, 220, 60);
        Assert.Equal(new Box(1102, 909 - 10 - 100, 420, 100), ShareLayout.PlacePanel(btn, MainMenu, 420, 100, 1920, 1080, 1f));
        // Button below the row: above the button would cover the row, so above the row.
        var below = new Box(838, 980, 220, 60);
        var p = ShareLayout.PlacePanel(below, MainMenu, 420, 100, 1920, 1080, 1f);
        Assert.Equal(910 - 10 - 100, p.Y);
        Assert.Equal(0f, ShareLayout.Overlap(p, MainMenu));
    }

    [Fact]
    public void PanelThatCannotAvoidEverythingOverlapsLeast()
    {
        // 1000x300: above runs off the top and below off the bottom; clamped, above covers the least.
        var row = new Box(300, 200, 260, 58);
        var btn = new Box(584, 199, 220, 60);
        var p = ShareLayout.PlacePanel(btn, row, 420, 200, 1000, 300, 1f);
        Assert.Equal(new Box(556, 24, 420, 200), p);
        var below = new Box(556, 76, 420, 200);   // the clamped below spot
        Assert.True(ShareLayout.Overlap(p, row) + ShareLayout.Overlap(p, btn) < ShareLayout.Overlap(below, row) + ShareLayout.Overlap(below, btn));
    }

    [Fact]
    public void NarrowViewportPicksASpotThatFits()
    {
        var row = new Box(520, 910, 260, 58);   // bottom-centre of 1300 wide
        var b = P(row, None, None, 1300, 1080, 1f);
        Assert.True(b.X >= 24 && b.Right <= 1300 - 24);
        Assert.False(b.Intersects(row));
    }

    [Fact]
    public void NoRowFallsBackToContinuesSpot()
    {
        var fallback = ShareLayout.FallbackRow(1920, 1080, 1f);
        Assert.Equal(Continue, fallback);
        Assert.Equal(P(Continue, None, None, 1920, 1080, 1f), P(None, None, None, 1920, 1080, 1f));
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
        var b = P(row, None, None, w, h, sc);
        Assert.False(b.Intersects(row));
        Assert.True(b.X >= 0 && b.Y >= 0 && b.Right <= w && b.Bottom <= h);
    }

    [Fact]
    public void BadScaleIsTreatedAsOne()
    {
        Assert.Equal(P(MainMenu, None, None, 1920, 1080, 1f), P(MainMenu, None, None, 1920, 1080, float.NaN));
        Assert.Equal(P(MainMenu, None, None, 1920, 1080, 1f), P(MainMenu, None, None, 1920, 1080, 0f));
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
