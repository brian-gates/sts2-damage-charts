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
        Assert.True(w.End(true));
        var r = Read();
        Assert.Equal("ended", r.GetProperty("status").GetString());
        Assert.Equal(2, r.GetProperty("seq").GetInt32());
        Assert.Equal(30, r.GetProperty("players")[0].GetProperty("hp").GetInt32());
        Assert.False(w.End(true));
        Assert.False(w.Tick(9, Input(hp: 1)));   // nothing after the end until the next fight
    }

    [Fact]
    public void End_with_a_final_state_writes_that_state()
    {
        var w = Writer();
        w.Tick(1, Input(hp: 30));
        Assert.True(w.End(true, Input(hp: 12)));
        Assert.Equal(12, Read().GetProperty("players")[0].GetProperty("hp").GetInt32());
    }

    [Fact]
    public void End_without_any_written_state_writes_nothing()
    {
        Assert.False(Writer().End(true));
        Assert.False(File.Exists(Path_));
    }

    [Fact]
    public void Begin_starts_a_new_fight_with_seq_back_at_1()
    {
        var w = Writer();
        w.Tick(1, Input());
        w.End(true);
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

    [Fact]
    public void An_unresolved_fight_writes_no_ended_state_and_can_still_end_later()
    {
        var w = Writer();
        w.Tick(1, Input(hp: 30));
        Assert.False(w.End(false));
        Assert.Equal("active", Read().GetProperty("status").GetString());
        Assert.True(w.End(true));
        Assert.Equal("ended", Read().GetProperty("status").GetString());
    }

    [Fact]
    public void A_failed_ended_write_is_retried_by_the_next_end()
    {
        var w = Writer();
        w.Tick(1, Input());
        Directory.Delete(Path.GetDirectoryName(Path_)!, true);
        File.WriteAllText(Path.GetDirectoryName(Path_)!, "in the way");   // the directory cannot be created
        Assert.False(w.End(true));
        File.Delete(Path.GetDirectoryName(Path_)!);
        Assert.True(w.End(true));
        var r = Read();
        Assert.Equal("ended", r.GetProperty("status").GetString());
        Assert.Equal(2, r.GetProperty("seq").GetInt32());
    }

    [Fact]
    public void A_resumed_fight_continues_the_seq_of_the_file_it_left()
    {
        var first = Writer();
        first.Tick(1, Input(hp: 50));
        first.Tick(2, Input(hp: 40));
        first.Tick(3, Input(hp: 30));   // seq 3 on disk, then the game quits
        var again = Writer();
        again.Begin();
        again.Resume("0123456789abcdef", 1);
        Assert.True(again.Tick(1, Input(hp: 20)));
        Assert.Equal(4, Read().GetProperty("seq").GetInt32());
    }

    [Fact]
    public void A_file_for_another_run_or_fight_or_a_malformed_one_does_not_change_the_seq()
    {
        var first = Writer();
        first.Tick(1, Input());
        first.Tick(2, Input(hp: 1));
        foreach (var (run, fight) in new[] { ("fedcba9876543210", 1), ("0123456789abcdef", 2) })
        {
            var w = Writer();
            w.Begin(); w.Resume(run, fight);
            Assert.True(w.Tick(1, Input(hp: 9)));
            Assert.Equal(1, Read().GetProperty("seq").GetInt32());
            first = Writer(); first.Tick(1, Input()); first.Tick(2, Input(hp: 1));   // restore seq 2 on disk
        }
        File.WriteAllText(Path_, "{ not json");
        var m = Writer();
        m.Begin(); m.Resume("0123456789abcdef", 1);
        Assert.True(m.Tick(1, Input()));
        Assert.Equal(1, Read().GetProperty("seq").GetInt32());
    }

    [Fact]
    public void SeqOf_reads_only_a_matching_sane_file()
    {
        string Doc(string run, int fight, string seq) => $"{{\"run_id\":\"{run}\",\"fight\":{fight},\"seq\":{seq}}}";
        Assert.Equal(7, LiveWriter.SeqOf(Doc("0123456789abcdef", 1, "7"), "0123456789abcdef", 1));
        Assert.Equal(0, LiveWriter.SeqOf(Doc("0123456789abcdef", 2, "7"), "0123456789abcdef", 1));
        Assert.Equal(0, LiveWriter.SeqOf(Doc("0123456789abcdef", 1, "-3"), "0123456789abcdef", 1));
        Assert.Equal(0, LiveWriter.SeqOf(Doc("0123456789abcdef", 1, "1e99"), "0123456789abcdef", 1));
        Assert.Equal(0, LiveWriter.SeqOf(Doc("0123456789abcdef", 1, "\"x\""), "0123456789abcdef", 1));
        Assert.Equal(0, LiveWriter.SeqOf("[]", "0123456789abcdef", 1));
        Assert.Equal(0, LiveWriter.SeqOf(null, "0123456789abcdef", 1));
        Assert.Equal(0, LiveWriter.SeqOf("", "0123456789abcdef", 1));
    }
}
