using System.Collections.Generic;
using STS2_DamageCharts;
using Xunit;

// Issue #42: stats from another run (a solo run after a co-op one) must never mix into this run.
public class RunKeyingTests
{
    private static (SourceSnapshot, ChartSnapshot) Combat(int players, long dealt)
    {
        var t = new DamageTracker();
        var labels = new string[players];
        for (int i = 0; i < players; i++) labels[i] = $"P{i}";
        t.Reset(players, labels, 0);
        for (int s = 0; s < players; s++) t.AddDealt(1, s, (int)dealt, "Strike");
        return (t.SourceSnapshot(), t.Snapshot());
    }

    [Fact]
    public void SwitchingRunsClearsTheAggregate()
    {
        var run = new RunAggregate();
        Assert.False(run.EnsureRun("coop"));
        var (s, c) = Combat(2, 10);
        run.Fold(s, c);
        Assert.False(run.EnsureRun("coop"));        // same run: kept
        Assert.Equal(1, run.FightCount);
        Assert.True(run.EnsureRun("solo"));          // another run: cleared
        Assert.Equal(0, run.FightCount);
        Assert.Equal("solo", run.RunKey);
        var (s1, c1) = Combat(1, 5);
        run.Fold(s1, c1);
        Assert.Equal(1, run.RunSourceSnapshot().PlayerCount);
        Assert.Equal(5, run.RunSourceSnapshot().PerPlayer[0].DealtTotal);
    }

    [Fact]
    public void UnknownIdentityKeepsTheCurrentData()
    {
        var run = new RunAggregate();
        run.EnsureRun("coop");
        var (s, c) = Combat(2, 10);
        run.Fold(s, c);
        Assert.False(run.EnsureRun(null));
        Assert.Equal(1, run.FightCount);
    }

    [Fact]
    public void APlayerCountChangeNeverFoldsIntoOldFights()
    {
        var run = new RunAggregate();
        var (s, c) = Combat(3, 10);
        run.Fold(s, c);
        var (s1, c1) = Combat(2, 4);
        run.Fold(s1, c1);
        Assert.Equal(1, run.FightCount);
        Assert.Equal(2, run.RunSourceSnapshot().PlayerCount);
        Assert.Single(run.EncounterStats(1));
    }

    [Fact]
    public void TryGetFightReturnsTheFoldedFight()
    {
        var run = new RunAggregate();
        var (s, c) = Combat(2, 10);
        run.Fold(s, c, new FightMeta { Floor = 4, FromEvent = true });
        Assert.True(run.TryGetFight(1, out var meta, out var src));
        Assert.Equal(4, meta.Floor);
        Assert.True(meta.FromEvent);
        Assert.Equal(1, meta.Rounds); // falls back to the last round with damage
        Assert.Equal(2, src.Length);
        Assert.False(run.TryGetFight(2, out _, out _));
    }

    [Fact]
    public void TrackerCountsHitsAndKeepsIdsPerPlayer()
    {
        var t = new DamageTracker();
        t.Reset(2, new[] { "A", "B" }, 0);
        t.AddDealt(1, 0, 6, "Strike", null, "CARD.STRIKE_SILENT");
        t.AddDealt(2, 0, 6, "Strike", null, "CARD.STRIKE_SILENT");
        t.AddDealt(1, 1, 6, "Strike", null, "CARD.STRIKE_IRONCLAD");   // same title, other player: own id
        t.AddTaken(1, 0, 3, "Louse", null, "MONSTER.LOUSE_RED");
        t.AddTaken(1, 0, 4, "Louse", null, "MONSTER.LOUSE_GREEN");     // same name, two models: ambiguous
        t.AddTaken(1, 1, 2, "Status");
        t.AddPotion(0, "Weak Potion", null, "POTION.WEAK_POTION");
        var snap = t.SourceSnapshot();
        var a = snap.PerPlayer[0];
        Assert.Equal(2, a.Dealt[0].Hits);
        Assert.Equal("CARD.STRIKE_SILENT", a.Dealt[0].Id);
        Assert.Equal("CARD.STRIKE_IRONCLAD", snap.PerPlayer[1].Dealt[0].Id);
        Assert.Equal(2, a.Taken[0].Hits);
        Assert.Null(a.Taken[0].Id);
        Assert.Null(snap.PerPlayer[1].Taken[0].Id);
        Assert.Equal("POTION.WEAK_POTION", a.Potions[0].Id);

        t.Reset(2, new[] { "A", "B" }, 0);                              // a new combat starts clean
        t.AddDealt(1, 0, 1, "Strike");
        Assert.Null(t.SourceSnapshot().PerPlayer[0].Dealt[0].Id);
        Assert.Equal(1, t.SourceSnapshot().PerPlayer[0].Dealt[0].Hits);
    }
}
