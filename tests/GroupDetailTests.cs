using System.Collections.Generic;
using STS2_DamageCharts;
using Xunit;

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

    // ----- Hit-testing -----

    [Theory]
    [InlineData(100f, 50f, 0)]   // first group's left edge
    [InlineData(139.9f, 50f, 0)]
    [InlineData(140f, 50f, 1)]   // next group starts exactly at left + groupW
    [InlineData(219f, 120f, 2)]  // bottom edge (axis label band) still counts
    [InlineData(220f, 50f, -1)]  // past the last group
    [InlineData(99f, 50f, -1)]   // left of the plot
    [InlineData(150f, 9f, -1)]   // above the plot
    [InlineData(150f, 121f, -1)] // below the label band
    public void GroupAt_MapsCursorToGroup(float mx, float my, int expected)
    {
        Assert.Equal(expected, GroupDetails.GroupAt(mx, my, left: 100f, top: 10f, bottom: 120f, groupW: 40f, groups: 3));
    }

    [Fact]
    public void GroupAt_NoGroupsOrZeroWidth_IsNone()
    {
        Assert.Equal(-1, GroupDetails.GroupAt(110f, 50f, 100f, 10f, 120f, 40f, 0));
        Assert.Equal(-1, GroupDetails.GroupAt(110f, 50f, 100f, 10f, 120f, 0f, 3));
        Assert.Equal(-1, GroupDetails.GroupAt(float.NaN, 50f, 100f, 10f, 120f, 40f, 3));
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
        Assert.Null(p0.Heal);
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
    public void FightDetail_KeepsEachFightsOwnBreakdownAndMeta()
    {
        var run = new RunAggregate();
        run.Fold(Snap(Player(5, 20, new[] { ("Strike", 30L), ("Bash", 10L) }, new[] { ("Jaw Worm", 8L) })),
                 Chart(1, 1, 2, 3),
                 new FightMeta { Floor = 2, Name = "Jaw Worm", Kind = FightKind.Normal });
        run.Fold(Snap(Player(0, 12, new[] { ("Poison", 50L) }, new[] { ("Lagavulin", 25L), ("Status", 5L) })),
                 Chart(1, 1, 2),
                 new FightMeta { Floor = 6, Name = "Lagavulin", Kind = FightKind.Elite, Rounds = 7 });

        var f1 = run.FightDetail(1)!;
        Assert.Equal("Fight 1 · Floor 2", f1.Title);
        Assert.Equal("Jaw Worm", f1.Subtitle);
        Assert.Equal("3 rounds", f1.Note);          // fell back to the last recorded round
        var p = Assert.Single(f1.Players);
        Assert.Equal(40, p.DealtTotal);
        Assert.Equal(("Strike", 75), (p.Dealt[0].Name, p.Dealt[0].Pct));
        Assert.Equal(8, p.TakenTotal);
        Assert.Equal(5L, p.Heal);
        Assert.Equal(20L, p.Block);

        var f2 = run.FightDetail(2)!;
        Assert.Equal(FightKind.Elite, f2.Kind);
        Assert.Equal("7 rounds", f2.Note);          // captured rounds win over the chart fallback
        Assert.Equal(("Lagavulin", 83), (f2.Players[0].Taken[0].Name, f2.Players[0].Taken[0].Pct));

        Assert.Null(run.FightDetail(0));
        Assert.Null(run.FightDetail(3));
    }

    [Fact]
    public void FightDetail_UnknownMeta_ShowsJustFightNumber()
    {
        var run = new RunAggregate();
        run.Fold(Snap(Player(0, 0, new[] { ("Strike", 6L) }, new (string, long)[0])), Chart(1));
        var d = run.FightDetail(1)!;
        Assert.Equal("Fight 1", d.Title);
        Assert.Null(d.Subtitle);
        Assert.Null(d.Note);
        Assert.Equal(FightKind.Unknown, d.Kind);
    }

    [Fact]
    public void FightDetail_Multiplayer_OneSectionPerPlayerWithLabels()
    {
        var run = new RunAggregate();
        run.Fold(Snap(Player(0, 0, new[] { ("A", 1L) }, new (string, long)[0]),
                      Player(0, 0, new[] { ("B", 2L) }, new (string, long)[0])), Chart(2, 1));
        var d = run.FightDetail(1)!;
        Assert.Equal(2, d.Players.Count);
        Assert.Equal(("Hero0", 0), (d.Players[0].Label, d.Players[0].Slot));
        Assert.Equal(("Hero1", 1), (d.Players[1].Label, d.Players[1].Slot));
    }

    [Fact]
    public void Clear_DropsFightDetails()
    {
        var run = new RunAggregate();
        run.Fold(Snap(Player(0, 0, new[] { ("A", 1L) }, new (string, long)[0])), Chart(1, 1));
        run.Clear();
        Assert.Null(run.FightDetail(1));
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

    [Fact]
    public void RoundsNote_Pluralizes()
    {
        Assert.Null(GroupDetails.RoundsNote(0));
        Assert.Equal("1 round", GroupDetails.RoundsNote(1));
        Assert.Equal("5 rounds", GroupDetails.RoundsNote(5));
    }
}
