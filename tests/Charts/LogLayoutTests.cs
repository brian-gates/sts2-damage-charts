using System.Collections.Generic;
using System.Linq;
using Xunit;
using STS2_DamageCharts.Charts;
using STS2_DamageCharts.Tracking;

public class LogLayoutTests
{
    private static LogEntry E(HitKind? kind, long amount) => new(1, "x", LogTone.Plain, kind, amount);

    [Fact]
    public void BarWidthIsProportionalToTheLargestAmount()
    {
        Assert.Equal(200f, LogLayout.BarWidth(24, 24, 200f));
        Assert.Equal(100f, LogLayout.BarWidth(12, 24, 200f), 3);
        Assert.Equal(50f, LogLayout.BarWidth(6, 24, 200f), 3);
    }

    [Fact]
    public void BarWidthNeverPassesTheMaxAndShowsSmallHits()
    {
        Assert.Equal(200f, LogLayout.BarWidth(99, 24, 200f));     // a stale scale can't overflow the row
        Assert.Equal(2f, LogLayout.BarWidth(1, 1000, 200f));      // a 1-HP hit is still visible
        Assert.Equal(0f, LogLayout.BarWidth(0, 24, 200f));
        Assert.Equal(0f, LogLayout.BarWidth(-3, 24, 200f));
        Assert.Equal(0f, LogLayout.BarWidth(5, 24, 0f));
        Assert.Equal(200f, LogLayout.BarWidth(5, 0, 200f));       // a zero scale reads as 1
    }

    [Fact]
    public void FitSizeShrinksToTheColumnAndNeverReturnsFullSizeForATinyOne()
    {
        float W(int size) => size * 10f;                  // the text is 10 px per font px
        Assert.Equal(20, LogLayout.FitSize(W, 20, 300f));  // fits as is
        Assert.Equal(15, LogLayout.FitSize(W, 20, 150f));  // shrinks to fit
        Assert.Equal(9, LogLayout.FitSize(W, 20, 50f));    // floor; the label trims the rest
        Assert.Equal(9, LogLayout.FitSize(W, 20, 0f));     // a collapsed column gets the floor, not 20
        Assert.Equal(9, LogLayout.FitSize(W, 20, -5f));
        Assert.Equal(8, LogLayout.FitSize(W, 8, 10f));     // already below the floor: unchanged
    }

    [Fact]
    public void PotionAndTextLinesHaveNoBar()
    {
        Assert.False(LogLayout.HasBar(HitKind.Potion));
        Assert.False(LogLayout.HasBar(null));
        foreach (var k in new[] { HitKind.Dealt, HitKind.Taken, HitKind.Absorbed, HitKind.Healed, HitKind.Buffered })
            Assert.True(LogLayout.HasBar(k));
    }

    [Fact]
    public void MaxAmountIgnoresPotionsAndTextLines()
    {
        var log = new[] { E(HitKind.Dealt, 6), E(HitKind.Potion, 50), E(null, 80), E(HitKind.Buffered, 24), E(HitKind.Taken, 9) };
        Assert.Equal(24, LogLayout.MaxAmount(log));
        Assert.Equal(1, LogLayout.MaxAmount(new LogEntry[0]));
    }

    [Fact]
    public void DividerCentersTheLabelBetweenTwoLines()
    {
        var d = LogLayout.Divider(10f, 210f, 60f, 8f);
        Assert.Equal(80f, d.LabelX, 3);
        Assert.Equal(10f, d.LeftLineX, 3);
        Assert.Equal(62f, d.LeftLineW, 3);      // 10 .. 72
        Assert.Equal(148f, d.RightLineX, 3);    // 80 + 60 + 8
        Assert.Equal(62f, d.RightLineW, 3);     // .. 210
    }

    [Fact]
    public void DividerDropsLinesThatDoNotFit()
    {
        var d = LogLayout.Divider(0f, 70f, 60f, 8f);
        Assert.Equal(0f, d.LeftLineW);
        Assert.Equal(0f, d.RightLineW);
        var wide = LogLayout.Divider(0f, 50f, 300f, 8f);   // a label wider than the row is clamped to it
        Assert.Equal(0f, wide.LabelX);
    }

    [Fact]
    public void FightSeparatorIsMarkedNotARound()
    {
        var e = LogEntry.FightSeparator(4);
        Assert.True(e.IsFightSeparator);
        Assert.Equal(4, e.Fight);
        Assert.Null(e.Kind);
        Assert.Equal("Fight 4", e.Text);
        Assert.False(E(HitKind.Dealt, 3).IsFightSeparator);
    }
}
