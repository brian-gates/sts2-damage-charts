using System.Collections.Generic;
using Xunit;
using STS2_DamageCharts.Tracking;

public class GroupDetailTests
{
    private static PlayerSources Player(long heal, long block, (string, long)[] dealt, (string, long)[] taken)
    {
        var ps = new PlayerSources { HealTotal = heal, BlockTotal = block };
        foreach (var (n, v) in dealt) { ps.Dealt.Add(new SourceEntry(n, v, null)); ps.DealtTotal += v; }
        foreach (var (n, v) in taken) { ps.Taken.Add(new SourceEntry(n, v, null)); ps.TakenTotal += v; }
        return ps;
    }

    private static SourceSnapshot Snap(params PlayerSources[] players)
    {
        var labels = new string[players.Length];
        for (int i = 0; i < labels.Length; i++) labels[i] = $"Hero{i}";
        return new SourceSnapshot(players, labels, players.Length, 0, new LogEntry[0]);
    }

    private static ChartSnapshot Chart(int players, params int[] rounds)
    {
        var rows = new List<ChartRow>();
        foreach (var r in rounds)
        {
            var segs = new List<DealtSeg>[players];
            for (int s = 0; s < players; s++) segs[s] = new List<DealtSeg>();
            rows.Add(new ChartRow(r, new int[players], new int[players], segs));
        }
        return new ChartSnapshot(rows, new string[players], players);
    }

    // ----- Per-round aggregation -----

    [Fact]
    public void AggregateBySource_FoldsHitsSortsByTotalKeepsFirstSeenOnTies()
    {
        var segs = new List<DealtSeg>
        {
            new(6, null, "Strike"), new(4, "res://bash.png", "Bash"), new(6, null, "Strike"),
            new(4, null, "Poison"), new(4, null, "Bash"),
        };
        var agg = GroupDetails.AggregateBySource(segs);
        Assert.Equal(3, agg.Count);
        Assert.Equal(("Strike", 12L, 2), (agg[0].Label, agg[0].Sum, agg[0].Count));
        Assert.Equal(("Bash", 8L, 2, "res://bash.png"), (agg[1].Label, agg[1].Sum, agg[1].Count, agg[1].Icon));
        Assert.Equal("Poison", agg[2].Label);
    }

    [Fact]
    public void ForRound_AllPlayers_DealtBySourceWithShareAndTakenTotal()
    {
        var segs = new[]
        {
            new List<DealtSeg> { new(9, null, "Strike"), new(3, null, "Poison") },
            new List<DealtSeg> { new(5, null, "Zap") },
        };
        var row = new ChartRow(4, new[] { 12, 5 }, new[] { 7, 0 }, segs);
        var d = GroupDetails.ForRound(row, new[] { "Ironclad (You)", "Silent" }, 2, null, showPct: true, maxRows: 5);

        Assert.Equal("Round 4", d.Title);
        Assert.Equal(2, d.Players.Count);
        var p0 = d.Players[0];
        Assert.Equal("Ironclad (You)", p0.Label);
        Assert.Equal(12, p0.DealtTotal);
        Assert.Equal(75, p0.Dealt[0].Pct);
        Assert.Equal(25, p0.Dealt[1].Pct);
        Assert.Equal(7, p0.TakenTotal);
        Assert.Empty(p0.Taken);          // no per-round taken breakdown is recorded
        Assert.Equal("Silent", d.Players[1].Label);
    }

    [Fact]
    public void ForRound_SingleSlot_PutsPlayerInTitleOnlyInMultiplayer()
    {
        var segs = new[] { new List<DealtSeg> { new(2, null, "A") }, new List<DealtSeg>() };
        var row = new ChartRow(1, new[] { 2, 0 }, new[] { 0, 3 }, segs);
        var d = GroupDetails.ForRound(row, new[] { "You", "Them" }, 2, 1, showPct: false, maxRows: 8);
        Assert.Equal("Round 1 · Them", d.Title);
        Assert.Single(d.Players);
        Assert.Equal("", d.Players[0].Label);
        Assert.Equal(3, d.Players[0].TakenTotal);

        var solo = GroupDetails.ForRound(new ChartRow(2, new[] { 2 }, new[] { 0 }, new[] { segs[0] }), new[] { "You" }, 1, 0, false, 8);
        Assert.Equal("Round 2", solo.Title);
    }

    // ----- Per-fight breakdown through the run aggregate -----

    [Fact]
    public void RunTotalsSumAbsorbedAcrossFights()
    {
        var run = new RunAggregate();
        for (int i = 0; i < 2; i++)
        {
            var snap = Snap(Player(0, 0, new[] { ("Strike", 10L) }, new[] { ("Jaw Worm", 3L) }));
            snap.PerPlayer[0].AbsorbedTotal = 5 + i;
            run.Fold(snap, Chart(1, 1, 2));
        }
        Assert.Equal(11L, run.RunSourceSnapshot().PerPlayer[0].AbsorbedTotal);
    }

    // ----- Fight naming -----

    [Theory]
    [InlineData("Boss", (int)FightKind.Boss)]
    [InlineData("elite", (int)FightKind.Elite)]
    [InlineData("Monster", (int)FightKind.Normal)]
    [InlineData("Event", (int)FightKind.Unknown)]
    [InlineData(null, (int)FightKind.Unknown)]
    public void KindFromRoomType(string? rt, int expected) => Assert.Equal((FightKind)expected, FightMeta.KindFromRoomType(rt));

    [Theory]
    [InlineData("Jaw Worm", false)]
    [InlineData("Lagavulin", false)]
    [InlineData("ENCOUNTERS.JAW_WORM.title", true)]
    [InlineData("JAW_WORM_WEAK", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void LooksLikeLocKey(string? s, bool expected) => Assert.Equal(expected, GroupDetails.LooksLikeLocKey(s));

    [Fact]
    public void JoinNames_DistinctCappedWithOverflowCount()
    {
        Assert.Null(GroupDetails.JoinNames(new string?[] { null, " " }));
        Assert.Equal("Louse", GroupDetails.JoinNames(new[] { "Louse", "Louse" }));
        Assert.Equal("Cultist, Louse +1", GroupDetails.JoinNames(new[] { "Cultist", "Louse", "Louse", "Slime" }));
    }

}
