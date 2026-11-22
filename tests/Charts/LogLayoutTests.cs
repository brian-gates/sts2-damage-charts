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

    // ===== Display rows =====

    private static LogEntry Line(int round) => new(round, "hit", LogTone.Plain, HitKind.Dealt, 3);

    [Fact]
    public void RecapRows_FightDividersRestartRoundTracking()
    {
        // As RunAggregate.Fold builds the recap log: fight 1 with 3 rounds, then fight 2.
        var run = new RunAggregate();
        var t = new DamageTracker();
        t.Reset(1, new[] { "you" }, 0);
        for (int r = 1; r <= 3; r++)
        {
            t.AddDealt(r, 0, 3, "Strike");
            t.AddHit(new Hit(r, HitKind.Dealt, 0, "Strike", null, 0, "Cultist", -1, 3));
        }
        run.Fold(t.SourceSnapshot(), t.Snapshot());
        t.Reset(1, new[] { "you" }, 0);
        t.AddDealt(1, 0, 5, "Bash");
        t.AddHit(new Hit(1, HitKind.Dealt, 0, "Bash", null, 0, "Louse", -1, 5));
        run.Fold(t.SourceSnapshot(), t.Snapshot());

        var log = run.RunSourceSnapshot().Log;
        var rows = new List<LogRow>();
        LogLayout.Rows(log, rows);
        var seq = rows.Select(r => r.Kind switch
        {
            LogRowKind.FightDivider => $"Fight {r.Number}",
            LogRowKind.RoundDivider => $"Round {r.Number}",
            _ => "line",
        }).ToArray();
        Assert.Equal(new[] { "Fight 1", "Round 1", "line", "Round 2", "line", "Round 3", "line", "Fight 2", "Round 1", "line" }, seq);
        Assert.Equal("Fight 2", log[rows[7].Entry].Text);
        Assert.False(LogLayout.HasBar(log[rows[0].Entry].Kind));   // separators get no bar
    }

    [Fact]
    public void CombatRows_ADividerPrecedesEachRound()
    {
        var rows = new List<LogRow> { new(LogRowKind.Line, 9, 9) };   // reused list is cleared
        LogLayout.Rows(new[] { Line(1), Line(1), Line(2) }, rows);
        Assert.Equal(new[] { LogRowKind.RoundDivider, LogRowKind.Line, LogRowKind.Line, LogRowKind.RoundDivider, LogRowKind.Line },
            rows.Select(r => r.Kind).ToArray());
        Assert.Equal(new[] { -1, 0, 1, -1, 2 }, rows.Select(r => r.Entry).ToArray());
    }

    [Fact]
    public void FightSeparatorIsMarkedNotARound()
    {
        var e = LogEntry.FightSeparator(4);
        Assert.True(e.IsFightSeparator);
        Assert.Equal(4, e.Fight);
        Assert.Null(e.Kind);
        Assert.Equal("Fight 4", e.Text);
        Assert.False(Line(4).IsFightSeparator);
    }
}
