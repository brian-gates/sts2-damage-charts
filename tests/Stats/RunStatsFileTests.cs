using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using STS2_DamageCharts.Forecast;
using STS2_DamageCharts.Stats;
using STS2_DamageCharts.Tracking;

public class RunStatsFileTests
{
    private const string A = "76561198000000001";
    private const string B = "76561198000000002";
    private const string Run = "ba18b6e0404293b2";

    private static PlayerSources Player((string, long, int, string?)[] dealt, (string, long, int, string?)[] taken,
        long heal = 0, long block = 0, params (string, long)[] potions)
    {
        var ps = new PlayerSources { HealTotal = heal, BlockTotal = block };
        foreach (var (n, v, h, id) in dealt) { ps.Dealt.Add(new SourceEntry(n, v, null, h, id)); ps.DealtTotal += v; }
        foreach (var (n, v, h, id) in taken) { ps.Taken.Add(new SourceEntry(n, v, null, h, id)); ps.TakenTotal += v; }
        foreach (var (n, c) in potions) ps.Potions.Add(new SourceEntry(n, c, null));
        return ps;
    }

    private static PlayerSources[] TwoPlayers() => new[]
    {
        Player(new[] { ("Strike", 63L, 7, (string?)"CARD.STRIKE_SILENT"), ("Poison", 77L, 5, "POWER.POISON_POWER") },
               new[] { ("Terror Eel", 6L, 1, (string?)null) }, heal: 6, block: 12, ("Weak Potion", 1L)),
        Player(new[] { ("Bash", 20L, 2, (string?)"CARD.BASH") }, Array.Empty<(string, long, int, string?)>()),
    };

    private static FightMeta Meta(int floor = 3, FightKind kind = FightKind.Normal, int rounds = 4, string? name = "Cultist, Louse")
        => new() { Floor = floor, Kind = kind, Rounds = rounds, Name = name };

    private static JsonObject Fight(int floor = 3) => RunStatsFile.BuildFight(Meta(floor), TwoPlayers(), new[] { A, B });

    // Every key anywhere must be one the server's schema knows (it rejects extras).
    private static void AssertOnlySpecKeys(JsonNode root)
    {
        var allowed = new Dictionary<string, string[]>
        {
            ["root"] = new[] { "v", "run_id", "recorded_by", "mod_version", "updated", "fights" },
            ["fight"] = new[] { "index", "floor", "encounter", "kind", "rounds", "players" },
            ["player"] = new[] { "steam_id", "dealt", "taken", "healed", "block", "potions" },
            ["list"] = new[] { "total", "sources" },
            ["source"] = new[] { "name", "id", "amount", "hits" },
        };
        void Check(JsonObject o, string level) { foreach (var kv in o) Assert.Contains(kv.Key, allowed[level]); }
        var r = root.AsObject();
        Check(r, "root");
        foreach (var f in r["fights"]!.AsArray())
        {
            Check(f!.AsObject(), "fight");
            foreach (var p in f["players"]!.AsArray())
            {
                Check(p!.AsObject(), "player");
                foreach (var side in new[] { "dealt", "taken" })
                {
                    Check(p[side]!.AsObject(), "list");
                    foreach (var s in p[side]!["sources"]!.AsArray()) Check(s!.AsObject(), "source");
                }
            }
        }
    }

    [Fact]
    public void SerializesTheSpecExampleShape()
    {
        var fights = new List<JsonObject>();
        RunStatsFile.Upsert(fights, Fight(), 1);
        string text = RunStatsFile.Serialize(Run, A, "1.3.1", 1794420000, fights);
        var root = JsonNode.Parse(text)!;

        Assert.Equal(new[] { "v", "run_id", "recorded_by", "mod_version", "updated", "fights" },
            root.AsObject().Select(kv => kv.Key).ToArray());
        Assert.Equal(1, (int)root["v"]!);
        Assert.Equal(Run, (string)root["run_id"]!);
        Assert.Equal(A, (string)root["recorded_by"]!);
        Assert.Equal(1794420000L, (long)root["updated"]!);
        var f = root["fights"]![0]!;
        Assert.Equal(new[] { "index", "floor", "encounter", "kind", "rounds", "players" }, f.AsObject().Select(kv => kv.Key).ToArray());
        Assert.Equal(1, (int)f["index"]!);
        Assert.Equal(3, (int)f["floor"]!);
        Assert.Equal("Cultist, Louse", (string)f["encounter"]!);
        Assert.Equal("monster", (string)f["kind"]!);
        Assert.Equal(4, (int)f["rounds"]!);
        var p = f["players"]![0]!;
        Assert.Equal(A, (string)p["steam_id"]!);
        Assert.Equal(140L, (long)p["dealt"]!["total"]!);
        var top = p["dealt"]!["sources"]![0]!;               // largest first
        Assert.Equal("Poison", (string)top["name"]!);
        Assert.Equal("POWER.POISON_POWER", (string)top["id"]!);
        Assert.Equal(77L, (long)top["amount"]!);
        Assert.Equal(5, (int)top["hits"]!);
        var eel = p["taken"]!["sources"]![0]!;
        Assert.True(eel.AsObject().ContainsKey("id"));
        Assert.Null(eel["id"]);                               // unknown id is an explicit null
        Assert.Equal(6L, (long)p["healed"]!);
        Assert.Equal(12L, (long)p["block"]!);
        Assert.Equal(new[] { "Weak Potion" }, p["potions"]!.AsArray().Select(n => (string)n!).ToArray());
        Assert.Equal(B, (string)f["players"]![1]!["steam_id"]!);
        AssertOnlySpecKeys(root);
    }

    [Fact]
    public void UnknownMetaIsNullAndModVersionFallsBackWhenManifestIsUnknown()
    {
        var f = RunStatsFile.BuildFight(new FightMeta(), TwoPlayers(), new[] { A, B });
        Assert.Null(f["floor"]);
        Assert.Null(f["encounter"]);
        Assert.Null(f["rounds"]);
        Assert.Equal("unknown", (string)f["kind"]!);
        Assert.Equal("unknown", RunStatsFile.KindName(null));
        // The server requires mod_version: an unreadable manifest falls back to the DLL's version, then the build tag.
        Assert.Equal("1.4.0", RunStatsFile.ModVersion(" 1.4.0 ", "1.3.9", "2026-11-15"));
        Assert.Equal("1.4.0", RunStatsFile.ModVersion(null, "1.4.0", "2026-11-15"));
        Assert.Equal("2026-11-15", RunStatsFile.ModVersion(null, null, "2026-11-15"));
        Assert.Equal("2026-11-15", RunStatsFile.ModVersion("", " ", "2026-11-15"));
        string version = RunStatsFile.ModVersion(null, null, "2026-11-15");
        var root = JsonNode.Parse(RunStatsFile.Serialize(Run, A, version, 1, new List<JsonObject> { f }))!;
        Assert.Equal("2026-11-15", (string)root["mod_version"]!);
        AssertOnlySpecKeys(root);
    }

    [Fact]
    public void HitsAreOmittedWhenNotCounted()
    {
        var ps = Player(new[] { ("Strike", 5L, 0, (string?)null) }, Array.Empty<(string, long, int, string?)>());
        var f = RunStatsFile.BuildFight(Meta(), new[] { ps, ps }, new[] { A, B });
        Assert.False(f["players"]![0]!["dealt"]!["sources"]![0]!.AsObject().ContainsKey("hits"));
    }

    [Theory]
    [InlineData(1, false, "monster")]   // FightKind.Normal
    [InlineData(2, false, "elite")]
    [InlineData(3, false, "boss")]
    [InlineData(0, false, "unknown")]
    [InlineData(1, true, "event")]
    [InlineData(2, true, "event")]
    public void KindNames(int kind, bool fromEvent, string expected) =>
        Assert.Equal(expected, RunStatsFile.KindName(new FightMeta { Kind = (FightKind)kind, FromEvent = fromEvent }));

    [Fact]
    public void CapsSourcesPlayersStringsAndPotions()
    {
        var many = Enumerable.Range(0, 70).Select(i => ($"Card{i}", (long)(i + 1), 1, (string?)null)).ToArray();
        var ps = Player(many, many, 0, 0, (new string('P', 100), 60L));
        var players = Enumerable.Repeat(ps, 10).ToArray();
        var ids = Enumerable.Range(0, 10).Select(i => $"7656119800000000{i}").ToArray();
        var f = RunStatsFile.BuildFight(Meta(name: new string('x', 120)), players, ids);

        Assert.Equal(80, ((string)f["encounter"]!).Length);
        Assert.Equal(RunStatsFile.MaxPlayers, f["players"]!.AsArray().Count);
        var p = f["players"]![0]!;
        Assert.Equal(RunStatsFile.MaxSources, p["dealt"]!["sources"]!.AsArray().Count);
        Assert.Equal(70L, (long)p["dealt"]!["sources"]![0]!["amount"]!);              // the largest kept
        Assert.Equal(Enumerable.Range(1, 70).Sum(), (long)p["dealt"]!["total"]!);     // total covers all
        Assert.Equal(RunStatsFile.MaxSources, p["potions"]!.AsArray().Count);
        Assert.Equal(80, ((string)p["potions"]![0]!).Length);
    }

    [Fact]
    public void ClipNeverSplitsASurrogatePair()
    {
        string s = new string('a', 79) + "😀";
        Assert.Equal(new string('a', 79), RunStatsFile.Clip(s));
    }

    [Fact]
    public void AmountsAreClampedToTheServerRange()
    {
        var ps = Player(new[] { ("Big", 5_000_000_000L, 1, (string?)null) }, Array.Empty<(string, long, int, string?)>(), heal: -3);
        var f = RunStatsFile.BuildFight(Meta(), new[] { ps, ps }, new[] { A, B });
        var p = f["players"]![0]!;
        Assert.Equal(RunStatsFile.MaxAmount, (long)p["dealt"]!["total"]!);
        Assert.Equal(RunStatsFile.MaxAmount, (long)p["dealt"]!["sources"]![0]!["amount"]!);
        Assert.Equal(0L, (long)p["healed"]!);
    }

    [Fact]
    public void DuplicateOrMissingSteamIdsGetNoSecondEntry()
    {
        var f = RunStatsFile.BuildFight(Meta(), new[] { TwoPlayers()[0], TwoPlayers()[1], TwoPlayers()[1] }, new[] { A, A, null });
        Assert.Single(f["players"]!.AsArray());
    }

    [Fact]
    public void AppendsInPlayOrderWithIncreasingIndex()
    {
        var fights = new List<JsonObject>();
        Assert.Equal(RunStatsFile.Outcome.Appended, RunStatsFile.Upsert(fights, Fight(3), 1));
        Assert.Equal(RunStatsFile.Outcome.Appended, RunStatsFile.Upsert(fights, Fight(5), 1));
        Assert.Equal(new[] { 1, 2 }, fights.Select(f => (int)f["index"]!).ToArray());
        Assert.Equal(new[] { 3, 5 }, fights.Select(f => (int)f["floor"]!).ToArray());
    }

    [Fact]
    public void AFightReplayedAfterAReloadReplacesTheStoredOne()
    {
        var fights = new List<JsonObject>();
        RunStatsFile.Upsert(fights, Fight(3), 1);
        RunStatsFile.Upsert(fights, Fight(5), 1);
        var replay = RunStatsFile.BuildFight(Meta(5, rounds: 9), TwoPlayers(), new[] { A, B });
        Assert.Equal(RunStatsFile.Outcome.Replaced, RunStatsFile.Upsert(fights, replay, 1));
        Assert.Equal(2, fights.Count);
        Assert.Equal(2, (int)fights[1]["index"]!);
        Assert.Equal(9, (int)fights[1]["rounds"]!);
    }

    [Fact]
    public void ASecondFightOnTheSameFloorIsAppended()
    {
        var fights = new List<JsonObject>();
        RunStatsFile.Upsert(fights, Fight(5), 1);
        Assert.Equal(RunStatsFile.Outcome.Appended, RunStatsFile.Upsert(fights, Fight(5), 2));
        Assert.Equal(2, fights.Count);
    }

    [Fact]
    public void UnknownFloorAlwaysAppends()
    {
        var fights = new List<JsonObject>();
        var f = RunStatsFile.BuildFight(new FightMeta(), TwoPlayers(), new[] { A, B });
        RunStatsFile.Upsert(fights, f, 1);
        Assert.Equal(RunStatsFile.Outcome.Appended, RunStatsFile.Upsert(fights, f, 1));
    }

    [Fact]
    public void AFullFileTakesNoMoreFights()
    {
        var fights = new List<JsonObject>();
        for (int i = 1; i <= RunStatsFile.MaxFights; i++) RunStatsFile.Upsert(fights, Fight(i), 1);
        Assert.Equal(RunStatsFile.Outcome.Full, RunStatsFile.Upsert(fights, Fight(999), 1));
        Assert.Null(RunStatsFile.AddFight(fights, Run, A, "1.4.0", 1, Meta(999), TwoPlayers(), new[] { A, B }, 1, out var o));
        Assert.Equal(RunStatsFile.Outcome.Full, o);
    }

    [Fact]
    public void ContinuingARunKeepsEarlierFightsFromTheFile()
    {
        string first = RunStatsFile.AddFight(new List<JsonObject>(), Run, A, "1.3.1", 100, Meta(3), TwoPlayers(), new[] { A, B }, 1, out _)!;
        var existing = RunStatsFile.ParseFights(first, Run)!;
        string second = RunStatsFile.AddFight(existing, Run, A, "1.3.1", 200, Meta(4), TwoPlayers(), new[] { A, B }, 1, out var o)!;
        Assert.Equal(RunStatsFile.Outcome.Appended, o);
        Assert.Single(existing); // the caller's list is not changed
        var root = JsonNode.Parse(second)!;
        Assert.Equal(200L, (long)root["updated"]!);
        Assert.Equal(new[] { 1, 2 }, root["fights"]!.AsArray().Select(f => (int)f!["index"]!).ToArray());
        Assert.Equal(new[] { 3, 4 }, root["fights"]!.AsArray().Select(f => (int)f!["floor"]!).ToArray());
        AssertOnlySpecKeys(root);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"v\":2,\"run_id\":\"ba18b6e0404293b2\",\"fights\":[]}")]
    [InlineData("{\"v\":1,\"run_id\":\"ffffffffffffffff\",\"fights\":[]}")]
    [InlineData("{\"v\":1,\"run_id\":\"ba18b6e0404293b2\"}")]
    public void ParseRejectsOtherFilesAndRuns(string json) => Assert.Null(RunStatsFile.ParseFights(json, Run));

    [Fact]
    public void ParseDropsKeysTheServerWouldReject()
    {
        string json = "{\"v\":1,\"run_id\":\"ba18b6e0404293b2\",\"extra\":1,\"fights\":[{\"index\":1,\"kind\":\"monster\",\"x\":1," +
            "\"players\":[{\"steam_id\":\"" + A + "\",\"y\":2,\"dealt\":{\"total\":1,\"z\":3,\"sources\":[{\"name\":\"S\",\"amount\":1,\"icon\":\"res://x\"}]}," +
            "\"taken\":{\"total\":0,\"sources\":[]}}]}]}";
        var fights = RunStatsFile.ParseFights(json, Run)!;
        var root = JsonNode.Parse(RunStatsFile.Serialize(Run, A, "1.4.0", 1, fights))!;
        AssertOnlySpecKeys(root);
        Assert.Equal("S", (string)root["fights"]![0]!["players"]![0]!["dealt"]!["sources"]![0]!["name"]!);
    }

    [Fact]
    public void OutOfOrderStoredIndexesAreRenumbered()
    {
        string json = "{\"v\":1,\"run_id\":\"ba18b6e0404293b2\",\"fights\":[" +
            "{\"index\":2,\"kind\":\"monster\",\"players\":[]},{\"index\":2,\"kind\":\"elite\",\"players\":[]}]}";
        var fights = RunStatsFile.ParseFights(json, Run)!;
        var root = JsonNode.Parse(RunStatsFile.Serialize(Run, A, "1.4.0", 1, fights))!;
        Assert.Equal(new[] { 1, 2 }, root["fights"]!.AsArray().Select(f => (int)f!["index"]!).ToArray());
    }

    [Fact]
    public void AFightThatWouldOverflowTheSizeCapLosesItsSourceListsFirst()
    {
        // Pad earlier fights so a full new fight can't fit under 1 MB but one without source lists can.
        var big = Enumerable.Range(0, 50).Select(i => (new string('n', 80) + i, 1L, 1, (string?)new string('i', 80))).ToArray();
        var fat = Enumerable.Repeat(Player(big, big), 8).ToArray();
        var ids = Enumerable.Range(0, 8).Select(i => $"7656119800000000{i}").ToArray();
        var existing = new List<JsonObject>();
        while (true)
        {
            var trial = new List<JsonObject>(existing);
            RunStatsFile.Upsert(trial, RunStatsFile.BuildFight(Meta(trial.Count + 1), fat, ids), 1);
            if (System.Text.Encoding.UTF8.GetByteCount(RunStatsFile.Serialize(Run, ids[0], "1.4.0", 1, trial)) > RunStatsFile.MaxBytes - 20_000) break;
            existing = trial;
        }
        string? text = RunStatsFile.AddFight(existing, Run, ids[0], "1.4.0", 1, Meta(500), fat, ids, 1, out var o);
        Assert.NotNull(text);
        Assert.Equal(RunStatsFile.Outcome.Appended, o);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text!) <= RunStatsFile.MaxBytes);
        var last = JsonNode.Parse(text!)!["fights"]!.AsArray().Last()!;
        Assert.True(last["players"]![0]!["dealt"]!["sources"]!.AsArray().Count < RunStatsFile.MaxSources);
        Assert.Equal(50L, (long)last["players"]![0]!["dealt"]!["total"]!);
    }
}
