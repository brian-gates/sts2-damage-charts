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
        var c = RoundCards.Columns(100f, 830f, 1f);
        Assert.Equal(240f, c.BarW);
        Assert.Equal(100f + 830f - 240f, c.BarX);
        Assert.Equal(c.BarX - 14f - 90f, c.AmountX);
        Assert.Equal(90f, c.AmountW);
        Assert.Equal(100f + 30f + 14f, c.TextX);
        Assert.Equal(c.AmountX - 14f - c.TextX, c.TextW);
        Assert.Equal(102f, c.IconX);
    }

    [Fact]
    public void ColumnsNarrowTheBarOnSmallCards()
    {
        var c = RoundCards.Columns(0f, 400f, 1f);
        Assert.Equal(120f, c.BarW, 3);             // 30% of the width
        var tiny = RoundCards.Columns(0f, 100f, 1f);
        Assert.Equal(0f, tiny.TextW);              // never negative
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
