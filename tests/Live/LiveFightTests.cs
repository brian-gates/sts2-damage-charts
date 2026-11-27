using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using STS2_DamageCharts.Stats;
using STS2_DamageCharts.Tracking;
using Xunit;

namespace STS2_DamageCharts.Tests.Live;

// The live fight number must be the index the damage file will give this fight, so the server can end the right one.
public class LiveFightTests
{
    private static JsonObject F(int index, int floor) => new() { ["index"] = index, ["floor"] = floor };

    [Fact]
    public void A_new_floor_gets_the_next_index_after_the_files_last()
    {
        var fights = new List<JsonObject> { F(1, 2), F(2, 3) };
        Assert.Equal(3, RunStatsFile.NextIndex(fights, floor: 5, floorOrdinal: 1));
        Assert.Equal(2, fights.Count);   // the file's list is left alone
    }

    [Fact]
    public void An_empty_file_starts_at_1()
        => Assert.Equal(1, RunStatsFile.NextIndex(new List<JsonObject>(), 4, 1));

    [Fact]
    public void A_fight_replayed_after_a_reload_keeps_the_stored_index()
    {
        var fights = new List<JsonObject> { F(1, 2), F(2, 3), F(3, 4) };
        Assert.Equal(2, RunStatsFile.NextIndex(fights, floor: 3, floorOrdinal: 1));
    }

    [Fact]
    public void A_second_fight_on_the_same_floor_is_appended()
    {
        var fights = new List<JsonObject> { F(1, 2), F(2, 3) };
        Assert.Equal(3, RunStatsFile.NextIndex(fights, floor: 3, floorOrdinal: 2));
    }

    [Fact]
    public void An_unknown_floor_is_appended()
        => Assert.Equal(2, RunStatsFile.NextIndex(new List<JsonObject> { F(1, 2) }, floor: 0, floorOrdinal: 1));

    [Fact]
    public void Unordered_stored_indexes_are_renumbered_by_position_like_the_file_is()
    {
        var fights = new List<JsonObject> { F(5, 2), F(5, 3) };   // not strictly increasing: Serialize renumbers 1, 2
        Assert.Equal(3, RunStatsFile.NextIndex(fights, floor: 9, floorOrdinal: 1));
    }

    [Fact]
    public void A_full_file_has_no_index()
    {
        var fights = Enumerable.Range(1, RunStatsFile.MaxFights).Select(i => F(i, i)).ToList();
        Assert.Null(RunStatsFile.NextIndex(fights, floor: 999, floorOrdinal: 1));
    }

    [Fact]
    public void The_trackers_totals_cover_dealt_taken_heal_block_and_damage_block_stopped()
    {
        var t = new DamageTracker();
        t.Reset(2, new[] { "A", "B" }, 0);
        t.AddDealt(1, 0, 10, "Strike");
        t.AddDealt(2, 0, 5, "Bash");
        t.AddTaken(1, 0, 4, "Jaw Worm");
        t.AddHealed(0, 3);
        t.AddBlock(0, 12);
        t.AddBlockStopped(0, 7);
        var a = t.Totals(0);
        Assert.Equal((15L, 4L, 3L, 12L, 7L), (a.Dealt, a.Taken, a.Healed, a.Blocked, a.BlockStopped));
        var b = t.Totals(1);
        Assert.Equal((0L, 0L, 0L, 0L, 0L), (b.Dealt, b.Taken, b.Healed, b.Blocked, b.BlockStopped));
        Assert.Equal(default, t.Totals(7));   // an unknown slot is all zero, not an error
        t.Reset(1, new[] { "A" }, 0);
        Assert.Equal(0L, t.Totals(0).BlockStopped);
    }
}
