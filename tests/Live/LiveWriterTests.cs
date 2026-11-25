using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using STS2_DamageCharts.Live;
using Xunit;

namespace STS2_DamageCharts.Tests.Live;

public sealed class LiveWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "live-writer-" + Guid.NewGuid().ToString("N"));
    private readonly HashSet<string> _notes = new();   // like ErrorLog.NoteOnce: a repeated message counts once
    private string Path_ => Path.Combine(_dir, "damage-charts", "live.json");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private LiveWriter Writer(Func<long>? clock = null) => new(Path_, m => _notes.Add(m), clock ?? (() => 1_760_000_000));

    private static LiveInput Input(int round = 1, string side = "player", int hp = 50) => new("0123456789abcdef", 1, "Jaw Worm", 3, round, side,
        new[] { new LivePlayer("76561198000000001", "CHARACTER.SILENT", hp, 70, 0, 0, 0, 0, 0, 0) },
        new[] { new LiveEnemy("MONSTER.JAW_WORM", "Jaw Worm", 40, 44, 0, null) });

    private JsonElement Read() => JsonDocument.Parse(File.ReadAllText(Path_)).RootElement;

    [Fact]
    public void The_first_tick_writes_with_seq_1_and_status_active()
    {
        var w = Writer();
        Assert.True(w.Tick(1, Input()));
        var r = Read();
        Assert.Equal(1, r.GetProperty("seq").GetInt32());
        Assert.Equal("active", r.GetProperty("status").GetString());
        Assert.Equal(1_760_000_000L, r.GetProperty("written_at").GetInt64());
        Assert.False(File.Exists(Path_ + ".tmp"));
    }

    [Fact]
    public void Unchanged_content_in_the_same_round_and_side_is_not_rewritten()
    {
        var w = Writer();
        Assert.True(w.Tick(1, Input()));
        Assert.False(w.Tick(2, Input()));
        Assert.Equal(1, Read().GetProperty("seq").GetInt32());
    }

    [Fact]
    public void A_content_change_writes_again_with_a_higher_seq()
    {
        var w = Writer();
        w.Tick(1, Input(hp: 50));
        Assert.True(w.Tick(2, Input(hp: 44)));
        Assert.Equal(2, Read().GetProperty("seq").GetInt32());
    }

    [Fact]
    public void A_round_or_side_change_writes_even_when_the_content_is_identical()
    {
        var w = Writer();
        w.Tick(1, Input(round: 1, side: "player"));
        Assert.True(w.Tick(2, Input(round: 1, side: "enemy")));
        Assert.True(w.Tick(3, Input(round: 2, side: "player")));
        Assert.Equal(3, Read().GetProperty("seq").GetInt32());
    }

    [Fact]
    public void At_most_one_write_per_frame()
    {
        var w = Writer();
        Assert.True(w.Tick(5, Input(hp: 50)));
        Assert.False(w.Tick(5, Input(hp: 10, round: 9)));
        Assert.Equal(1, Read().GetProperty("seq").GetInt32());
    }

    [Fact]
    public void End_writes_the_last_state_as_ended_once()
    {
        var w = Writer();
        w.Tick(1, Input(hp: 30));
        Assert.True(w.End());
        var r = Read();
        Assert.Equal("ended", r.GetProperty("status").GetString());
        Assert.Equal(2, r.GetProperty("seq").GetInt32());
        Assert.Equal(30, r.GetProperty("players")[0].GetProperty("hp").GetInt32());
        Assert.False(w.End());
        Assert.False(w.Tick(9, Input(hp: 1)));   // nothing after the end until the next fight
    }

    [Fact]
    public void End_with_a_final_state_writes_that_state()
    {
        var w = Writer();
        w.Tick(1, Input(hp: 30));
        Assert.True(w.End(Input(hp: 12)));
        Assert.Equal(12, Read().GetProperty("players")[0].GetProperty("hp").GetInt32());
    }

    [Fact]
    public void End_without_any_written_state_writes_nothing()
    {
        Assert.False(Writer().End());
        Assert.False(File.Exists(Path_));
    }

    [Fact]
    public void Begin_starts_a_new_fight_with_seq_back_at_1()
    {
        var w = Writer();
        w.Tick(1, Input());
        w.End();
        w.Begin();
        Assert.True(w.Tick(10, Input()));
        var r = Read();
        Assert.Equal(1, r.GetProperty("seq").GetInt32());
        Assert.Equal("active", r.GetProperty("status").GetString());
    }

    [Fact]
    public void A_write_failure_is_logged_once_and_never_thrown()
    {
        Directory.CreateDirectory(Path_);   // the target path is a directory: the move fails
        var w = Writer();
        Assert.False(w.Tick(1, Input()));
        Assert.False(w.Tick(2, Input(hp: 1)));
        Assert.Single(_notes);
    }

    [Fact]
    public void A_builder_failure_is_logged_once_and_never_thrown()
    {
        var w = Writer();
        var bad = Input() with { RunId = "not a run id" };
        Assert.False(w.Tick(1, bad));
        Assert.False(w.Tick(2, bad));
        Assert.Single(_notes);
        Assert.False(File.Exists(Path_));
    }
}
