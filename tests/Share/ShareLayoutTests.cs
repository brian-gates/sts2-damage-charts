using Xunit;
using STS2_DamageCharts.Share;

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

    [Fact]
    public void IconAndLabelAreCentredAsAPair()
    {
        // 220x60 button, 18 px glyph, 9 px gap, 100 px of text: the pair is 127 wide.
        var (x, y, shift) = ShareLayout.IconRow(220, 60, 18, 9, 100);
        Assert.Equal(46.5f, x);
        Assert.Equal(21f, y);
        Assert.Equal(13.5f, shift);
        // The text's centre (label centred in the button, then shifted) sits right after the glyph and gap.
        float textLeft = 220 / 2f + shift - 100 / 2f;
        Assert.Equal(x + 18 + 9, textLeft);
        Assert.Equal(220 - (textLeft + 100), x);   // equal margins either side
        Assert.Equal((0f, 0f, 0f), ShareLayout.IconRow(220, 60, 0, 9, 100));
        Assert.Equal((0f, 0f, 0f), ShareLayout.IconRow(220, 60, float.NaN, 9, 100));
    }

    [Fact]
    public void IconIsAboutTheCapHeightAndScales()
    {
        Assert.Equal(18.2f, ShareLayout.IconSize(26), 3);
        Assert.Equal(2 * ShareLayout.IconSize(26), ShareLayout.IconSize(52), 3);
        Assert.Equal(0f, ShareLayout.IconSize(0));
        Assert.Equal(0f, ShareLayout.IconSize(float.PositiveInfinity));
    }

    [Fact]
    public void GlyphHasOneNodeLeftAndTwoRightInsideItsSquare()
    {
        var g = ShareLayout.ShareGlyph(20);
        Assert.True(g.LX < g.TX && g.TX == g.BX);           // left node, two on the right
        Assert.True(g.TY < g.LY && g.LY < g.BY);            // top, middle, bottom
        Assert.Equal(10f, g.LY);
        Assert.Equal(g.LY - g.TY, g.BY - g.LY, 3);           // symmetric about the middle
        foreach (var (cx, cy) in new[] { (g.LX, g.LY), (g.TX, g.TY), (g.BX, g.BY) })
        {
            Assert.InRange(cx - g.Radius, -0.001f, 20.001f);
            Assert.InRange(cx + g.Radius, -0.001f, 20.001f);
            Assert.InRange(cy - g.Radius, -0.001f, 20.001f);
            Assert.InRange(cy + g.Radius, -0.001f, 20.001f);
        }
        Assert.True(g.Line >= 1f && g.Line < 2 * g.Radius);
        Assert.Equal(default, ShareLayout.ShareGlyph(0));
        Assert.Equal(2 * g.Radius, ShareLayout.ShareGlyph(40).Radius, 3);
    }

    // The resting rects at 1920x1080: Continue, Leaderboard (same spot), Main Menu, View Run.
    private static readonly Box[] Rests = { Continue, Continue, MainMenu, new Box(702, 910, 260, 58) };

    [Fact]
    public void RowIsTheUnionOfEveryButtonAtRest()
    {
        Assert.Equal(new Box(702, 910, 388, 58), ShareLayout.Row(Rests));
        Assert.Equal(Continue, ShareLayout.Row(new[] { None, Continue, None }));
        Assert.True(ShareLayout.Row(new Box[0]).IsEmpty);
    }

    [Fact]
    public void StaysPutAcrossTheScreensPages()
    {
        // The row doesn't depend on which buttons are shown, so neither does the spot: page one (Continue) and
        // page two (Main Menu, run summary above the row) place Share identically.
        var row = ShareLayout.Row(Rests);
        int spot = ShareLayout.NoSpot;
        var page1 = ShareLayout.Place(row, None, None, 1920, 1080, 1f, ref spot);
        var summary = new Box(250, 239, 1420, 640);   // ends above the row
        var page2 = ShareLayout.Place(row, summary, None, 1920, 1080, 1f, ref spot);
        var leaderboard = new Box(708, 402, 512, 440);
        var daily = ShareLayout.Place(row, None, leaderboard, 1920, 1080, 1f, ref spot);
        Assert.Equal(new Box(1114, 909, 220, 60), page1);
        Assert.Equal(page1, page2);
        Assert.Equal(page1, daily);
        Assert.False(page1.Intersects(MainMenu) || page1.Intersects(Continue) || page1.Intersects(Rests[3]));
    }

    [Fact]
    public void RowFreezesOnceRectsAreRealAndTheScreenHoldsStill()
    {
        var f = new RowFreeze();
        Assert.False(f.Step(true, 0, 0, 1, 1, 0));
        Assert.False(f.Step(true, 0, 0, 1, 1, 16));
        Assert.True(f.Step(true, 0, 0, 1, 1, 33));     // three frames the same
        Assert.True(f.Frozen);
        // Frozen for the rest of the screen: later changes (a page change, a button hiding) don't unfreeze it.
        Assert.True(f.Step(false, 50, 50, 2, 2, 5000));
    }

    [Fact]
    public void RowWaitsWhileTheScreenAnimatesIn()
    {
        var f = new RowFreeze();
        for (int i = 0; i < 20; i++)   // an open tween: scale grows each frame
            Assert.False(f.Step(true, 0, 0, 0.8f + i * 0.01f, 0.8f + i * 0.01f, i * 16));
        Assert.False(f.Step(true, 0, 0, 1, 1, 400));
        Assert.False(f.Step(true, 0, 0, 1, 1, 416));
        Assert.True(f.Step(true, 0, 0, 1, 1, 432));
    }

    [Fact]
    public void RowWaitsForRealRestRectsThenGivesUpAfterTwoSeconds()
    {
        var f = new RowFreeze();
        for (double t = 0; t < RowFreeze.MaxWaitMs; t += 16)
            Assert.False(f.Step(false, 0, 0, 1, 1, t));   // a button still without a resting rect
        Assert.True(f.Step(false, 0, 0, 1, 1, RowFreeze.MaxWaitMs));
        Assert.True(new RowFreeze().Step(false, 0, 0, 1, 1, double.NaN));   // a bad clock never waits forever
    }

    // ----- Share fight beside Share run, and under the end-of-combat summary -----

    [Fact]
    public void SecondButtonSitsUnderTheFirst()
    {
        var run = new Box(1102, 909, 220, 60);
        Assert.Equal(new Box(1102, 909 + 60 + 12, 220, 60), ShareLayout.PlaceSecond(run, MainMenu, None, None, 1920, 1080, 1f));
    }

    [Fact]
    public void SecondButtonGoesAboveWhenBelowDoesNotFit()
    {
        var run = new Box(1102, 980, 220, 60);   // too low for another button under it
        var b = ShareLayout.PlaceSecond(run, MainMenu, None, None, 1920, 1080, 1f);
        Assert.Equal(new Box(1102, 980 - 12 - 60, 220, 60), b);
        Assert.False(b.Intersects(run));
        Assert.False(b.Intersects(MainMenu));
    }

    [Fact]
    public void SecondButtonNeverCoversTheRowOrContent()
    {
        // Below the row (the Below spot): under it runs off the screen, above it is the row, so beside it.
        var run = new Box(838, 980, 220, 60);
        var summary = new Box(0, 0, 1920, 500);
        var b = ShareLayout.PlaceSecond(run, MainMenu, summary, None, 1920, 1080, 1f);
        Assert.False(b.Intersects(run));
        Assert.False(b.Intersects(MainMenu));
        Assert.False(b.Intersects(summary));
        Assert.Equal(run.Y, b.Y);
    }

    [Fact]
    public void NearSitsBelowTheAnchorLeftAligned()
    {
        var summary = new Box(28, 262, 527, 566);
        Assert.Equal(new Box(28, 838, 165, 45), ShareLayout.PlaceNear(summary, summary, 165, 45, 1920, 1080, 1f));
    }

    [Fact]
    public void NearAvoidsTheAnchorAndWhatItMustNotCover()
    {
        // A short screen: below the summary doesn't fit, so beside it, bottom-aligned.
        var summary = new Box(28, 100, 527, 566);
        var b = ShareLayout.PlaceNear(summary, summary, 165, 45, 1280, 700, 1f);
        Assert.Equal(new Box(28 + 527 + 10, 100 + 566 - 45, 165, 45), b);
        // A message by that button: never over the summary or the button.
        var avoid = summary.Union(b);
        var m = ShareLayout.PlaceNear(b, avoid, 420, 80, 1280, 700, 1f);
        Assert.Equal(0f, ShareLayout.Overlap(m, avoid));
    }
}
