using System.Collections.Generic;
using System.Linq;
using Xunit;
using STS2_DamageCharts.Charts;
using STS2_DamageCharts.Tracking;

public class RoundCardsTests
{
    private static readonly CardMetrics M = CardMetrics.Scaled(1f);   // the mockup's own px

    private static LogEntry L(int round, HitKind kind, long amount) => new(round, "x", LogTone.Plain, kind, amount);

    [Fact]
    public void CardHeightMatchesTheMockup()
    {
        // padding 14+14, header 26, gap 4, split 8 + 6, then each row: gap 4 + 36.
        Assert.Equal(28f + 26f + 4f + 8f + 6f, RoundCards.CardHeight(0, M));
        Assert.Equal(72f + 3 * 40f, RoundCards.CardHeight(3, M));
        Assert.Equal(2f * (72f + 40f), RoundCards.CardHeight(1, CardMetrics.Scaled(2f)), 3);
    }

    [Fact]
    public void NativePaddingChangesOnlyThePads()
    {
        // On the game's hover-tip frame the card uses its text insets (PadX 24, PadY 17 at 1080p).
        var native = M with { PadX = 24f, PadY = 17f };
        Assert.Equal(RoundCards.CardHeight(2, M) + 2f * 3f, RoundCards.CardHeight(2, native));
        Assert.Equal(RoundCards.RowY(1, M) + 3f, RoundCards.RowY(1, native));
        Assert.Equal(M.Step, native.Step);   // scrolling unchanged
    }

    [Fact]
    public void RowsFollowTheSplitBar()
    {
        Assert.Equal(14f + 26f + 4f, RoundCards.SplitY(M));
        Assert.Equal(44f + 8f + 6f + 4f, RoundCards.RowY(0, M));
        Assert.Equal(62f + 2 * 40f, RoundCards.RowY(2, M));
        // The last row ends at the card's bottom padding.
        Assert.Equal(RoundCards.CardHeight(3, M) - M.PadY, RoundCards.RowY(2, M) + M.RowH);
    }

    [Fact]
    public void Build_OneCardPerRoundWithItsSums()
    {
        var log = new[]
        {
            L(1, HitKind.Dealt, 6), L(1, HitKind.Dealt, 3), L(1, HitKind.Taken, 4),
            L(2, HitKind.Healed, 6), L(2, HitKind.Buffered, 11), L(2, HitKind.Taken, 8), L(2, HitKind.Potion, -1),
        };
        var items = new List<CardItem> { default };   // reused list is cleared
        float h = RoundCards.Build(log, M, items);
        Assert.Equal(2, items.Count);
        var (a, b) = (items[0], items[1]);
        Assert.Equal((1, 0, 3, 9L, 4L), (a.Number, a.First, a.Count, a.Dealt, a.Taken));
        Assert.Equal((2, 3, 4, 0L, 8L), (b.Number, b.First, b.Count, b.Dealt, b.Taken));   // saved and healed aren't taken
        Assert.Equal(0f, a.Y);
        Assert.Equal(a.H + M.CardGap, b.Y);
        Assert.Equal(b.Y + b.H, h);
    }

    [Fact]
    public void Build_RecapFightDividersSplitCards()
    {
        var log = new[]
        {
            LogEntry.FightSeparator(1), L(1, HitKind.Dealt, 5), L(2, HitKind.Dealt, 5),
            LogEntry.FightSeparator(2), L(1, HitKind.Taken, 3),
        };
        var items = new List<CardItem>();
        RoundCards.Build(log, M, items);
        Assert.Equal(new[] { "Fight 1", "Round 1", "Round 2", "Fight 2", "Round 1" },
            items.Select(i => i.Kind == CardItemKind.FightDivider ? $"Fight {i.Number}" : $"Round {i.Number}").ToArray());
        Assert.Equal(M.DividerH, items[0].H);
        Assert.Equal(3, items[3].First);
    }

    [Fact]
    public void Build_EmptyLogHasNoContent()
    {
        var items = new List<CardItem>();
        Assert.Equal(0f, RoundCards.Build(new LogEntry[0], M, items));
        Assert.Empty(items);
    }

    [Fact]
    public void ColumnsMatchTheMockupGrid()
    {
        // A 1280x720 card content box ~ 830 px wide: 30 | text | 90 | 240, gaps 14.
        var c = RoundCards.Columns(100f, 830f, 1f, 240f);
        Assert.Equal(240f, c.BarW);
        Assert.Equal(100f + 830f - 240f, c.BarX);
        Assert.Equal(c.BarX - 14f - 90f, c.AmountX);
        Assert.Equal(90f, c.AmountW);
        Assert.Equal(100f + 30f + 14f, c.TextX);
        Assert.Equal(c.AmountX - 14f - c.TextX, c.TextW);
        Assert.Equal(102f, c.IconX);
    }

    [Fact]
    public void ColumnsGiveTheBarWayBeforeTheTextGoesNegative()
    {
        var c = RoundCards.Columns(0f, 400f, 1f, 120f);
        Assert.Equal(120f, c.BarW);                // the frame's bar width when it fits
        var tiny = RoundCards.Columns(0f, 200f, 1f, 240f);
        Assert.Equal(0f, tiny.TextW);              // never negative
        Assert.True(tiny.BarX >= tiny.AmountX + tiny.AmountW);
    }

    // ===== The full view's frame at real viewports (review I1) =====

    // The view's own scale: UiTheme.Scale (viewport height / 1080, clamped 0.75..2) x ui_scale, x 1.5 per mockup px.
    private static float U(float vh, float uiScale) => 1.5f * System.Math.Clamp(vh / 1080f, 0.75f, 2.0f) * uiScale;

    public static IEnumerable<object[]> Viewports()
    {
        foreach (var (w, h) in new[] { (1280f, 720f), (1440f, 900f), (1024f, 768f), (1920f, 1080f), (1920f, 1200f),
                                       (1920f, 1440f), (2560f, 1440f), (3440f, 1440f) })
            foreach (var ui in new[] { 1.0f, 1.25f, 1.5f })
                yield return new object[] { w, h, ui };
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public void FullFrameNeverOverlapsAndKeepsTheTextColumn(float vw, float vh, float ui)
    {
        float u0 = U(vh, ui);
        var f = RoundCards.Frame(vw, u0);
        float u = f.U;
        Assert.True(u <= u0 + 1e-3f);
        // Left to right: pad | sidebar | gap | log | pad, exactly filling the width.
        Assert.Equal(f.Pad + f.SideW + f.Gap, f.LogX, 2);
        Assert.Equal(vw, f.LogX + f.LogW + f.Pad, 1);
        Assert.True(f.SideW >= RoundCards.SideMin * u - 0.01f && f.SideW <= RoundCards.SideMax * u + 0.01f);
        Assert.True(f.Pad >= RoundCards.PadMin * u - 0.01f);
        // The card row: icon | text | amount | bar, in order, inside the card, text at least its minimum.
        float left = f.LogX + RoundCards.CardPadX * u, width = f.LogW - 2f * RoundCards.CardPadX * u;
        var c = RoundCards.Columns(left, width, u, f.BarW);
        Assert.True(c.TextW >= RoundCards.TextMin * u - 0.5f, $"text {c.TextW} < {RoundCards.TextMin * u}");
        Assert.True(c.IconX >= left - 0.01f);
        Assert.True(c.TextX + c.TextW <= c.AmountX + 0.01f);
        Assert.True(c.AmountX + c.AmountW <= c.BarX + 0.01f);
        Assert.True(c.BarX + c.BarW <= left + width + 0.01f);
        Assert.True(c.BarW >= RoundCards.BarMin * u - 0.01f);
    }

    // The compact summary's log column: its panel is 480 px wide (log 442 px after padding and the scrollbar), at
    // ui_scale 0.5..2 (SummaryU 0.7 per ui_scale step), and narrower widths for safety.
    [Theory]
    [InlineData(442f, 0.7f)]
    [InlineData(442f, 0.875f)]
    [InlineData(442f, 1.05f)]
    [InlineData(442f, 1.4f)]
    [InlineData(300f, 0.7f)]
    [InlineData(200f, 0.7f)]
    public void SummaryLogColumnKeepsTheTextColumn(float width, float u0)
    {
        var (u, barW) = RoundCards.LogColumn(width, u0);
        Assert.True(u <= u0 + 1e-4f);
        var c = RoundCards.Columns(0f, width - 2f * RoundCards.CardPadX * u, u, barW);
        Assert.True(c.TextW >= RoundCards.TextMin * u - 0.5f, $"text {c.TextW} < {RoundCards.TextMin * u}");
        Assert.True(c.TextX + c.TextW <= c.AmountX + 0.01f);
        Assert.True(c.AmountX + c.AmountW <= c.BarX + 0.01f);
        Assert.True(c.BarX + c.BarW <= width - 2f * RoundCards.CardPadX * u + 0.01f);
        Assert.InRange(barW, RoundCards.BarMin * u - 0.01f, RoundCards.BarMax * u + 0.01f);
    }

    [Fact]
    public void SummaryLogColumnAtTheDefaultSize()
    {
        var (u, barW) = RoundCards.LogColumn(442f, 0.7f);
        Assert.Equal(0.7f, u);                               // the requested scale fits
        Assert.Equal((80f + (442f / 0.7f - 478f)) * 0.7f, barW, 2);   // the bar takes the slack first (153.4 of 160)
        var (u2, bar2) = RoundCards.LogColumn(300f, 0.7f);   // narrower: the scale shrinks, bar at its minimum
        Assert.True(u2 < 0.7f);
        Assert.Equal(80f * u2, bar2, 2);
    }

    [Fact]
    public void FullFrameIsTheMockupAt1080p()
    {
        var f = RoundCards.Frame(1920f, 1.5f);
        Assert.Equal(1.5f, f.U);
        Assert.Equal(60f, f.Pad, 3);
        Assert.Equal(495f, f.SideW, 3);
        Assert.Equal(48f, f.Gap, 3);
        Assert.Equal(360f, f.BarW, 3);
    }

    [Fact]
    public void NarrowViewportsShrinkBarThenSidebarThenScale()
    {
        // 1920x1440 (4:3) at ui_scale 1.5: the review's collapsing case.
        float u0 = U(1440f, 1.5f);                        // 3.0
        var f = RoundCards.Frame(1920f, u0);
        Assert.True(f.U < u0);                            // even the minimums don't fit at the requested scale
        Assert.Equal(RoundCards.BarMin * f.U, f.BarW, 2);
        Assert.Equal(RoundCards.SideMin * f.U, f.SideW, 2);
        // Narrower than 1080p's 16:9: the text's extra room goes first (1920 leaves it 300 px over its minimum),
        // then the bar track; the sidebar and scale stay.
        var g = RoundCards.Frame(1920f - 300f - 60f, 1.5f);
        Assert.Equal(1.5f, g.U);
        Assert.Equal(495f, g.SideW, 3);
        Assert.Equal(300f, g.BarW, 2);
    }

    [Fact]
    public void SplitBarSharesTheWidth()
    {
        Assert.Equal((69f, 31f), RoundCards.SplitBar(69, 31, 100f));
        Assert.Equal((100f, 0f), RoundCards.SplitBar(9, 0, 100f));
        Assert.Equal((0f, 0f), RoundCards.SplitBar(0, 0, 100f));
        Assert.Equal((0f, 0f), RoundCards.SplitBar(3, 4, 0f));
    }

    [Fact]
    public void ScrollingInRowSteps()
    {
        Assert.Equal(10, RoundCards.VisibleSteps(400f, M));      // 40 px steps
        Assert.Equal(0, RoundCards.MaxOffset(300f, 400f, M));
        Assert.Equal(3, RoundCards.MaxOffset(500f, 400f, M));     // 100 px over: ceil(100 / 40)
        Assert.Equal(80f, RoundCards.ScrollY(2, 500f, 400f, M));
        Assert.Equal(100f, RoundCards.ScrollY(3, 500f, 400f, M)); // the last step stops at the end
        Assert.Equal(0f, RoundCards.ScrollY(5, 300f, 400f, M));
    }

    [Theory]
    [InlineData(8, 2, 3, 2, 3)]   // both fit
    [InlineData(6, 10, 10, 3, 3)] // half each
    [InlineData(7, 10, 10, 4, 3)] // the odd slot goes to dealt
    [InlineData(6, 1, 10, 1, 5)]  // dealt's unused half goes to taken
    [InlineData(6, 10, 0, 6, 0)]  // and taken's to dealt
    [InlineData(0, 4, 4, 0, 0)]
    public void SidebarSourcesShareTheSlots(int slots, int dealt, int taken, int expD, int expT)
    {
        Assert.Equal((expD, expT), RoundCards.SplitSources(slots, dealt, taken));
    }
}

public class SidebarPlanTests
{
    // Mockup px: a row is 29 tall, rows 44 apart at most, 33 at least.
    private static (int D, int T, int More, float Pitch) S(float avail, int d, int t) => RoundCards.SidebarPlan(avail, d, t, 29f, 44f, 33f);

    [Fact]
    public void EverySourceShowsWhenItFitsAtTheMinimumPitch()
    {
        // The real-game case: 2 dealt + 3 taken in 170 px (29 + 4 x 35.25): all five, spread to fill.
        var p = S(170f, 2, 3);
        Assert.Equal((2, 3, 0), (p.D, p.T, p.More));
        Assert.Equal(35.25f, p.Pitch, 3);
    }

    [Fact]
    public void RowsSpreadNoFurtherThanTheMaximumPitch()
    {
        var p = S(1000f, 2, 3);
        Assert.Equal((2, 3, 0, 44f), (p.D, p.T, p.More, p.Pitch));
    }

    [Fact]
    public void WhenOutOfRoomTheLastSlotSaysHowManyMore()
    {
        // 29 + 3 x 33 = 128: four slots of five sources: three rows and "+ 2 more".
        var p = S(130f, 2, 3);
        Assert.Equal(33f, p.Pitch);
        Assert.Equal(3, p.D + p.T);
        Assert.Equal(2, p.More);
        Assert.Equal((2, 1), (p.D, p.T));          // dealt keeps its half (rounded up), taken the rest
        // A lone side takes every shown slot.
        var lone = S(130f, 0, 9);
        Assert.Equal((0, 3, 6), (lone.D, lone.T, lone.More));
    }

    [Fact]
    public void FewerThanTwoSlotsShowNothing()
    {
        // One slot can't hold a row and its "+ N more": nothing (and no heading) rather than a bare count.
        var one = S(40f, 2, 3);
        Assert.Equal((0, 0, 5), (one.D, one.T, one.More));
        var none = S(20f, 2, 3);
        Assert.Equal((0, 0, 5), (none.D, none.T, none.More));
        var single = S(29f, 1, 0);                 // one source in one slot fits outright
        Assert.Equal((1, 0, 0), (single.D, single.T, single.More));
        var empty = S(500f, 0, 0);
        Assert.Equal((0, 0, 0), (empty.D, empty.T, empty.More));
    }
}
