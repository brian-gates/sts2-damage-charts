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
            ["root"] = new[] { "v", "run_id", "recorded_by", "mod_version", "game_version", "updated", "fights" },
            ["fight"] = new[] { "index", "floor", "encounter", "kind", "rounds", "players", "log", "log_dropped", "forecast_misses" },
            ["player"] = new[] { "steam_id", "dealt", "taken", "healed", "block", "absorbed", "buffered", "potions" },
            ["list"] = new[] { "total", "sources" },
            ["source"] = new[] { "name", "id", "amount", "hits" },
            ["miss"] = new[] { "round", "min", "max", "actual", "hidden", "block", "buffer", "intangible", "osty", "wuppy", "items" },
            ["item"] = new[] { "kind", "label", "hits", "per_hit", "conditional" },
        };
        void Check(JsonObject o, string level) { foreach (var kv in o) Assert.Contains(kv.Key, allowed[level]); }
        var r = root.AsObject();
        Check(r, "root");
        foreach (var f in r["fights"]!.AsArray())
        {
            Check(f!.AsObject(), "fight");
            if (f["forecast_misses"] is JsonArray misses)
                foreach (var m in misses)
                {
                    Check(m!.AsObject(), "miss");
                    foreach (var it in m["items"]!.AsArray()) Check(it!.AsObject(), "item");
                }
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
    public void AbsorbedIsWrittenOnlyWhenAPetTookHits()
    {
        var players = TwoPlayers();
        players[0].AbsorbedTotal = 14;
        var f = RunStatsFile.BuildFight(Meta(), players, new[] { A, B });
        Assert.Equal(14L, (long)f["players"]![0]!["absorbed"]!);
        Assert.False(f["players"]![1]!.AsObject().ContainsKey("absorbed"));  // omitted, not 0
    }

    [Fact]
    public void BufferedIsWrittenOnlyWhenABufferSavedHp()
    {
        var players = TwoPlayers();
        players[1].BufferedTotal = 24;
        var f = RunStatsFile.BuildFight(Meta(), players, new[] { A, B });
        Assert.Equal(24L, (long)f["players"]![1]!["buffered"]!);
        Assert.False(f["players"]![0]!.AsObject().ContainsKey("buffered"));  // omitted, not 0
        Assert.Equal(0L, (long)f["players"]![1]!["taken"]!["total"]!);         // saved HP is not taken
    }

    [Fact]
    public void BufferedSurvivesADroppedLog()
    {
        var players = TwoPlayers();
        players[0].BufferedTotal = 9;
        var f = RunStatsFile.BuildFight(Meta(), players, new[] { A, B }, new[] { H(HitKind.Buffered, 0, "Cultist", null, -1, "you", 0, 9) },
            false, 0, withLog: false);
        Assert.True((bool)f["log_dropped"]!);
        Assert.Equal(9L, (long)f["players"]![0]!["buffered"]!);
    }

    [Fact]
    public void GameVersionIsWrittenWhenKnown()
    {
        var fights = new List<JsonObject> { Fight() };
        var root = JsonNode.Parse(RunStatsFile.Serialize(Run, A, "1.6.0", 1, fights, "v0.114.0"))!;
        Assert.Equal(new[] { "v", "run_id", "recorded_by", "mod_version", "game_version", "updated", "fights" },
            root.AsObject().Select(kv => kv.Key).ToArray());
        Assert.Equal("v0.114.0", (string)root["game_version"]!);
        var none = JsonNode.Parse(RunStatsFile.Serialize(Run, A, "1.6.0", 1, fights, "  "))!;
        Assert.False(none.AsObject().ContainsKey("game_version"));
        var clipped = JsonNode.Parse(RunStatsFile.Serialize(Run, A, "1.6.0", 1, fights, new string('v', 100)))!;
        Assert.Equal(RunStatsFile.MaxString, ((string)clipped["game_version"]!).Length);
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
        Assert.Null(RunStatsFile.AddFight(fights, Run, A, "1.4.0", 1, Meta(999), TwoPlayers(), new[] { A, B }, null, false, 1, out var o));
        Assert.Equal(RunStatsFile.Outcome.Full, o);
    }

    [Fact]
    public void ContinuingARunKeepsEarlierFightsFromTheFile()
    {
        string first = RunStatsFile.AddFight(new List<JsonObject>(), Run, A, "1.3.1", 100, Meta(3), TwoPlayers(), new[] { A, B }, null, false, 1, out _)!;
        var existing = RunStatsFile.ParseFights(first, Run)!;
        string second = RunStatsFile.AddFight(existing, Run, A, "1.3.1", 200, Meta(4), TwoPlayers(), new[] { A, B }, null, false, 1, out var o)!;
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
    public void ParseKeepsBufferedAndForecastMisses()
    {
        var players = TwoPlayers();
        players[0].BufferedTotal = 7;
        string text = RunStatsFile.AddFight(new List<JsonObject>(), Run, A, "1.6.0", 1, Meta(), players, new[] { A, B },
            null, false, 1, out _, new[] { Miss(2) }, "v0.114.0")!;
        var fights = RunStatsFile.ParseFights(text, Run)!;
        Assert.Equal(7L, (long)fights[0]["players"]![0]!["buffered"]!);
        Assert.Single(fights[0]["forecast_misses"]!.AsArray());
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
        string? text = RunStatsFile.AddFight(existing, Run, ids[0], "1.4.0", 1, Meta(500), fat, ids, null, false, 1, out var o);
        Assert.NotNull(text);
        Assert.Equal(RunStatsFile.Outcome.Appended, o);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text!) <= RunStatsFile.MaxBytes);
        var last = JsonNode.Parse(text!)!["fights"]!.AsArray().Last()!;
        Assert.True(last["players"]![0]!["dealt"]!["sources"]!.AsArray().Count < RunStatsFile.MaxSources);
        Assert.Equal(50L, (long)last["players"]![0]!["dealt"]!["total"]!);
    }

    // ===== Forecast misses =====

    private static ForecastMiss Miss(int round, int items = 1) => new(round, 6, 9, 15, false, 4, 1, true, 0, 0,
        Enumerable.Range(0, items).Select(i => new ForecastMissItem("attack", "Cultist " + i, 2, 5, i % 2 == 1)).ToArray());

    private static int MissTotal(string text) =>
        JsonNode.Parse(text)!["fights"]!.AsArray().Sum(f => f!["forecast_misses"] is JsonArray a ? a.Count : 0);

    [Fact]
    public void ForecastMissesAreWrittenInTheSpecShape()
    {
        string text = RunStatsFile.AddFight(new List<JsonObject>(), Run, A, "1.6.0", 1, Meta(), TwoPlayers(), new[] { A, B },
            null, false, 1, out _, new[] { Miss(3, items: 2) })!;
        var root = JsonNode.Parse(text)!;
        AssertOnlySpecKeys(root);
        var m = root["fights"]![0]!["forecast_misses"]![0]!;
        Assert.Equal(new[] { "round", "min", "max", "actual", "hidden", "block", "buffer", "intangible", "osty", "wuppy", "items" },
            m.AsObject().Select(kv => kv.Key).ToArray());
        Assert.Equal(3, (int)m["round"]!);
        Assert.Equal(6L, (long)m["min"]!);
        Assert.Equal(9L, (long)m["max"]!);
        Assert.Equal(15L, (long)m["actual"]!);
        Assert.False((bool)m["hidden"]!);
        Assert.Equal(4L, (long)m["block"]!);
        Assert.Equal(1L, (long)m["buffer"]!);
        Assert.Equal(1, (int)m["intangible"]!);   // the server reads it as an amount
        var it = m["items"]![1]!;
        Assert.Equal("attack", (string)it["kind"]!);
        Assert.Equal("Cultist 1", (string)it["label"]!);
        Assert.Equal(2L, (long)it["hits"]!);
        Assert.Equal(5L, (long)it["per_hit"]!);
        Assert.True((bool)it["conditional"]!);
    }

    [Fact]
    public void AFightWithoutMissesHasNoKey()
    {
        var f = RunStatsFile.BuildFight(Meta(), TwoPlayers(), new[] { A, B }, misses: System.Array.Empty<ForecastMiss>());
        Assert.False(f.ContainsKey("forecast_misses"));
    }

    [Fact]
    public void MissValuesAreClampedForTheServer()
    {
        var bad = new ForecastMiss(-1, 9, 4, -2, true, -1, 0, false, 0, 0,
            new[] { new ForecastMissItem("", new string('l', 100), -1, 3, false), new ForecastMissItem(new string('k', 30), "x", 1, 1, false) });
        var m = RunStatsFile.BuildFight(Meta(), TwoPlayers(), new[] { A, B }, misses: new[] { bad })["forecast_misses"]![0]!;
        Assert.Equal(0, (int)m["round"]!);
        Assert.Equal(9L, (long)m["max"]!);       // never below min
        Assert.Equal(0L, (long)m["actual"]!);
        Assert.Equal(0L, (long)m["block"]!);
        Assert.Equal("other", (string)m["items"]![0]!["kind"]!);
        Assert.Equal(RunStatsFile.MaxString, ((string)m["items"]![0]!["label"]!).Length);
        Assert.Equal(0L, (long)m["items"]![0]!["hits"]!);
        Assert.Equal(RunStatsFile.MaxMissKind, ((string)m["items"]![1]!["kind"]!).Length);
    }

    [Fact]
    public void MissesAndTheirItemsAreCappedPerFight()
    {
        var many = Enumerable.Range(0, 25).Select(r => Miss(r, items: 30)).ToArray();
        var f = RunStatsFile.BuildFight(Meta(), TwoPlayers(), new[] { A, B }, misses: many);
        Assert.Equal(RunStatsFile.MaxMisses, f["forecast_misses"]!.AsArray().Count);
        Assert.Equal(RunStatsFile.MaxMissItems, f["forecast_misses"]![0]!["items"]!.AsArray().Count);
    }

    [Fact]
    public void TheFileHoldsAtMost20MissesOverAllFights()
    {
        var fifteen = Enumerable.Range(1, 15).Select(r => Miss(r)).ToArray();
        string first = RunStatsFile.AddFight(new List<JsonObject>(), Run, A, "1.6.0", 1, Meta(3), TwoPlayers(), new[] { A, B },
            null, false, 1, out _, fifteen)!;
        Assert.Equal(15, MissTotal(first));

        var ten = Enumerable.Range(1, 10).Select(r => Miss(r)).ToArray();
        string second = RunStatsFile.AddFight(RunStatsFile.ParseFights(first, Run)!, Run, A, "1.6.0", 2, Meta(4), TwoPlayers(), new[] { A, B },
            null, false, 1, out _, ten)!;
        Assert.Equal(20, MissTotal(second));
        var fights = JsonNode.Parse(second)!["fights"]!.AsArray();
        Assert.Equal(15, fights[0]!["forecast_misses"]!.AsArray().Count);     // the earlier fight is untouched
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, fights[1]!["forecast_misses"]!.AsArray().Select(m => (int)m!["round"]!).ToArray());

        // A third fight finds no room left: no key at all.
        string third = RunStatsFile.AddFight(RunStatsFile.ParseFights(second, Run)!, Run, A, "1.6.0", 3, Meta(5), TwoPlayers(), new[] { A, B },
            null, false, 1, out _, ten)!;
        Assert.Equal(20, MissTotal(third));
        Assert.False(JsonNode.Parse(third)!["fights"]![2]!.AsObject().ContainsKey("forecast_misses"));
    }

    [Fact]
    public void AReplayedFightsOldMissesDoNotCountAgainstItself()
    {
        var twenty = Enumerable.Range(1, 20).Select(r => Miss(r)).ToArray();
        string first = RunStatsFile.AddFight(new List<JsonObject>(), Run, A, "1.6.0", 1, Meta(3), TwoPlayers(), new[] { A, B },
            null, false, 1, out _, twenty)!;
        string replay = RunStatsFile.AddFight(RunStatsFile.ParseFights(first, Run)!, Run, A, "1.6.0", 2, Meta(3), TwoPlayers(), new[] { A, B },
            null, false, 1, out var o, new[] { Miss(7), Miss(8) })!;
        Assert.Equal(RunStatsFile.Outcome.Replaced, o);
        Assert.Equal(2, MissTotal(replay));
    }

    [Fact]
    public void AddFight_DropsMissesLast_WhenNearCap()
    {
        var existing = FileOfSize(RunStatsFile.MaxBytes - 1500);
        var misses = Enumerable.Range(1, 20).Select(r => Miss(r, items: 20)).ToArray();
        string? text = RunStatsFile.AddFight(existing, Run, A, "1.6.0", 1, Meta(2), TwoPlayers(), new[] { A, B },
            null, false, 1, out var o, misses);
        Assert.NotNull(text);
        Assert.Equal(RunStatsFile.Outcome.Appended, o);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text!) <= RunStatsFile.MaxBytes);
        var last = JsonNode.Parse(text!)!["fights"]!.AsArray().Last()!;
        Assert.False(last.AsObject().ContainsKey("forecast_misses"));
        Assert.Equal(140L, (long)last["players"]![0]!["dealt"]!["total"]!);   // totals always kept
    }

    // ===== The hit log =====

    private static Hit H(HitKind kind, int slot, string? src, string? srcId, int srcSlot, string? tgt, int tgtSlot, long amount, int round = 1)
        => new(round, kind, slot, src, srcId, srcSlot, tgt, tgtSlot, amount);

    [Fact]
    public void BuildFight_WritesCompactLog()
    {
        var hits = new[]
        {
            H(HitKind.Dealt, 0, "Strike", "CARD.STRIKE_SILENT", 0, "Cultist", -1, 6),
            H(HitKind.Taken, 0, "Cultist", null, -1, "you", 0, 9),
            H(HitKind.Potion, 1, "Fire Potion", "POTION.FIRE_POTION", -1, "Cultist", -1, -1, round: 3),
        };
        var f = RunStatsFile.BuildFight(Meta(), TwoPlayers(), new[] { A, B }, hits, false);
        Assert.Equal("{\"players\":[\"" + A + "\",\"" + B + "\"],\"names\":[\"Strike\",\"Cultist\",\"Fire Potion\"],"
            + "\"ids\":[\"CARD.STRIKE_SILENT\",null,\"POTION.FIRE_POTION\"],"
            + "\"hits\":[[1,0,0,0,1,6],[1,1,0,1,-2,9],[3,4,1,2,1,-1]],\"truncated\":false}",
            f["log"]!.ToJsonString());
        Assert.Null(f["log_dropped"]);
    }

    [Fact]
    public void Log_ResolvesEachKindsSides()
    {
        // Dealt: the source is the card, not the dealer. Healed: no target. A player other than the hit's own is a ref.
        var hits = new[]
        {
            H(HitKind.Dealt, 0, "you", "CARD.X", 0, "Cultist", -1, 5),         // SourceSlot == Slot: named source
            H(HitKind.Healed, 1, null, null, -1, "Ally", 1, 4),                // target -1, "Ally" not a name
            H(HitKind.Absorbed, 0, "Cultist", null, -1, "Osty", -1, 3),        // pet is a named target
            H(HitKind.Taken, 1, "you", null, 0, "Ally", 1, 2),                 // another player hit them: source ref -2
        };
        var log = RunStatsFile.BuildFight(Meta(), TwoPlayers(), new[] { A, B }, hits, false)["log"]!;
        Assert.Equal(new[] { "you", "Cultist", "Osty" }, log["names"]!.AsArray().Select(n => (string)n!).ToArray());
        Assert.Equal("[[1,0,0,0,1,5],[1,3,1,-1,-1,4],[1,2,0,1,2,3],[1,1,1,-2,-3,2]]", log["hits"]!.ToJsonString());
    }

    [Fact]
    public void Log_WritesBufferedHitsLikeTakenOnes()
    {
        var hits = new[]
        {
            H(HitKind.Buffered, 0, "Cultist", "MONSTER.CULTIST", -1, "you", 0, 24, 2),
            H(HitKind.Buffered, 1, null, null, -1, "Ally", 1, 6, 3),       // attacker unknown: source -1
            H(HitKind.Buffered, 1, "you", null, 0, "Ally", 1, 5, 3),       // another player's hit: source ref
        };
        var log = JsonNode.Parse(RunStatsFile.BuildFight(Meta(), TwoPlayers(), new[] { A, B }, hits)["log"]!.ToJsonString())!;
        var rows = log["hits"]!.AsArray().Select(h => h!.AsArray().Select(v => (long)v!).ToArray()).ToArray();
        Assert.Equal(new long[] { 2, 5, 0, 0, -2, 24 }, rows[0]);
        Assert.Equal(new long[] { 3, 5, 1, -1, -3, 6 }, rows[1]);
        Assert.Equal(new long[] { 3, 5, 1, -2, -3, 5 }, rows[2]);
        Assert.Equal(new[] { "Cultist" }, log["names"]!.AsArray().Select(n => (string)n!).ToArray());
        Assert.Equal("MONSTER.CULTIST", (string)log["ids"]![0]!);
    }

    [Fact]
    public void Log_NegativeAmountOfANonPotionLineIsZero()
    {
        // -1 means "no amount", which the server accepts only on potion lines.
        var hits = new[]
        {
            H(HitKind.Dealt, 0, "Strike", null, 0, "Cultist", -1, -5),
            H(HitKind.Potion, 0, "Fire Potion", null, -1, "Cultist", -1, 20),
        };
        var log = RunStatsFile.BuildFight(Meta(), TwoPlayers(), new[] { A, B }, hits, false)["log"]!;
        Assert.Equal("[[1,0,0,0,1,0],[1,4,0,2,1,-1]]", log["hits"]!.ToJsonString());
    }

    [Fact]
    public void Log_LeavesOutPlayersTheFightDoesNotWrite()
    {
        // Slot 1 has no PlayerSources, so it is not in "players" and the server would reject a log naming it.
        var hits = new[]
        {
            H(HitKind.Dealt, 1, "Bash", null, 1, "Cultist", -1, 8),
            H(HitKind.Dealt, 0, "Strike", null, 0, "Cultist", -1, 6),
            H(HitKind.Taken, 0, "Cultist", null, 1, "you", 0, 3),              // source is the unwritten player
        };
        var f = RunStatsFile.BuildFight(Meta(), new PlayerSources?[] { TwoPlayers()[0], null }, new[] { A, B }, hits, false);
        var log = f["log"]!;
        Assert.Equal(new[] { A }, log["players"]!.AsArray().Select(n => (string)n!).ToArray());
        Assert.Equal("[[1,0,0,0,1,6],[1,1,0,-1,-2,3]]", log["hits"]!.ToJsonString());
    }

    [Fact]
    public void Log_CapsHitsNamesAndClipsStrings()
    {
        var many = Enumerable.Range(0, 650).Select(i => H(HitKind.Dealt, 0, "S" + i, null, 0, new string('t', 120), -1, 1)).ToArray();
        var log = RunStatsFile.BuildFight(Meta(), TwoPlayers(), new[] { A, B }, many, false)["log"]!;
        Assert.Equal(600, log["hits"]!.AsArray().Count);
        Assert.True((bool)log["truncated"]!);
        Assert.Equal(log["names"]!.AsArray().Count, log["ids"]!.AsArray().Count);
        Assert.True(log["names"]!.AsArray().Count <= 600);
        Assert.All(log["names"]!.AsArray(), n => Assert.True(((string)n!).Length <= 80));
        // The tracker's own truncation flag passes through.
        var few = RunStatsFile.BuildFight(Meta(), TwoPlayers(), new[] { A, B }, many.Take(2).ToArray(), true)["log"]!;
        Assert.True((bool)few["truncated"]!);
    }

    [Fact]
    public void NullHitsMeansNoLog()
    {
        var f = RunStatsFile.BuildFight(Meta(), TwoPlayers(), new[] { A, B }, null, false);
        Assert.Null(f["log"]);
        Assert.Null(f["log_dropped"]);
    }

    [Fact]
    public void BuildFight_WritesEmptyLogWhenNoHits()
    {
        const string empty = "{\"players\":[],\"names\":[],\"ids\":[],\"hits\":[],\"truncated\":false}";
        Assert.Equal(empty, RunStatsFile.BuildFight(Meta(), TwoPlayers(), new[] { A, B }, Array.Empty<Hit>(), false)["log"]!.ToJsonString());
        // Every hit filtered out (its player has no sources): still a log.
        var hits = new[] { H(HitKind.Dealt, 1, "Bash", null, 1, "Cultist", -1, 8) };
        var f = RunStatsFile.BuildFight(Meta(), new PlayerSources?[] { TwoPlayers()[0], null }, new[] { A, B }, hits, false);
        Assert.Equal(empty, f["log"]!.ToJsonString());
        Assert.Null(f["log_dropped"]);
    }

    [Fact]
    public void BuildFight_LogMatchesTotals()
    {
        // Fed through the tracker as the game does: a total and its hit together.
        var t = new DamageTracker();
        t.Reset(2, new[] { "you", "Ally" }, 0);
        void Dealt(int round, int slot, int amt, string src, string id)
        {
            t.AddDealt(round, slot, amt, src, null, id);
            t.AddHit(H(HitKind.Dealt, slot, src, id, slot, "Cultist", -1, amt, round));
        }
        void Taken(int round, int slot, int amt, string src)
        {
            t.AddTaken(round, slot, amt, src);
            t.AddHit(H(HitKind.Taken, slot, src, null, -1, "you", slot, amt, round));
        }
        Dealt(1, 0, 6, "Strike", "CARD.STRIKE_SILENT");
        Dealt(1, 0, 9, "Poison", "POWER.POISON_POWER");
        Dealt(2, 0, 4, "Strike", "CARD.STRIKE_SILENT");
        Dealt(2, 1, 11, "Bash", "CARD.BASH");
        Taken(1, 0, 7, "Cultist");
        Taken(2, 1, 5, "Louse");
        t.AddAbsorbed(1, 3);
        t.AddHit(H(HitKind.Absorbed, 1, "Louse", null, -1, "Osty", -1, 3, 2));
        t.AddHealed(0, 5);
        t.AddHit(H(HitKind.Healed, 0, null, null, -1, "you", 0, 5, 2));
        t.AddPotion(1, "Fire Potion", null, "POTION.FIRE_POTION");
        t.AddHit(H(HitKind.Potion, 1, "Fire Potion", "POTION.FIRE_POTION", -1, "Cultist", -1, -1, 2));
        t.AddBuffered(0, 24);   // saved HP: its own kind, outside the dealt/taken/absorbed/healed sums
        t.AddHit(H(HitKind.Buffered, 0, "Cultist", "MONSTER.CULTIST", -1, "you", 0, 24, 2));

        var snap = t.SourceSnapshot();
        var f = RunStatsFile.BuildFight(Meta(), snap.PerPlayer, new[] { A, B }, snap.FightHits, snap.HitsTruncated);
        var log = f["log"]!;
        Assert.False((bool)log["truncated"]!);
        var logPlayers = log["players"]!.AsArray().Select(n => (string)n!).ToList();
        foreach (var p in f["players"]!.AsArray())
        {
            int idx = logPlayers.IndexOf((string)p!["steam_id"]!);
            long Sum(int kind) => log["hits"]!.AsArray().Select(h => h!.AsArray())
                .Where(h => (int)h[2]! == idx && (int)h[1]! == kind).Sum(h => (long)h[5]!);
            Assert.Equal((long)p["dealt"]!["total"]!, Sum(0));
            Assert.Equal((long)p["taken"]!["total"]!, Sum(1));
            Assert.Equal(p["absorbed"] is { } a ? (long)a : 0L, Sum(2));
            Assert.Equal((long)p["healed"]!, Sum(3));
            Assert.Equal(p["buffered"] is { } bu ? (long)bu : 0L, Sum(5));
        }
        Assert.Equal(24L, (long)f["players"]![0]!["buffered"]!);
        Assert.Equal(11L, (long)f["players"]![1]!["dealt"]!["total"]!); // the fixture really exercised the totals
    }

    private static List<JsonObject> FileOfSize(long target)
    {
        // One earlier fight, padded (it is only ever read back, so a long encounter name is fine) to `target` bytes.
        var pad = new JsonObject { ["index"] = 1, ["floor"] = 1, ["encounter"] = "", ["players"] = new JsonArray() };
        var list = new List<JsonObject> { pad };
        long baseSize = System.Text.Encoding.UTF8.GetByteCount(RunStatsFile.Serialize(Run, A, "1.4.0", 1, list));
        pad["encounter"] = new string('p', (int)(target - baseSize));
        return list;
    }

    [Fact]
    public void AddFight_DropsLogBeforeSources_WhenNearCap()
    {
        var existing = FileOfSize(RunStatsFile.MaxBytes - 1500);
        var hits = Enumerable.Range(0, 80).Select(i => H(HitKind.Dealt, 0, "Strike", "CARD.STRIKE_SILENT", 0, "Cultist", -1, 1, 1 + i / 10)).ToArray();

        // Premise: the fight fits with no log, and does not with it.
        var withLog = new List<JsonObject>(existing);
        RunStatsFile.Upsert(withLog, RunStatsFile.BuildFight(Meta(2), TwoPlayers(), new[] { A, B }, hits, false), 1);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(RunStatsFile.Serialize(Run, A, "1.4.0", 1, withLog)) > RunStatsFile.MaxBytes);

        string? text = RunStatsFile.AddFight(existing, Run, A, "1.4.0", 1, Meta(2), TwoPlayers(), new[] { A, B }, hits, false, 1, out var o);
        Assert.NotNull(text);
        Assert.Equal(RunStatsFile.Outcome.Appended, o);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text!) <= RunStatsFile.MaxBytes);
        var last = JsonNode.Parse(text!)!["fights"]!.AsArray().Last()!;
        Assert.True((bool)last["log_dropped"]!);
        Assert.Null(last["log"]);
        Assert.Equal(2, last["players"]![0]!["dealt"]!["sources"]!.AsArray().Count);
        Assert.Equal(140L, (long)last["players"]![0]!["dealt"]!["total"]!);
    }

    [Fact]
    public void AddFight_LeavesEarlierFightsLogsAlone()
    {
        var hits = new[] { H(HitKind.Dealt, 0, "Strike", "CARD.STRIKE_SILENT", 0, "Cultist", -1, 6) };
        string first = RunStatsFile.AddFight(new List<JsonObject>(), Run, A, "1.5.2", 100, Meta(3), TwoPlayers(), new[] { A, B }, hits, false, 1, out _)!;
        var existing = RunStatsFile.ParseFights(first, Run)!;
        Assert.NotNull(existing[0]["log"]); // a stored log survives the read-back
        string before = existing[0].ToJsonString();

        string second = RunStatsFile.AddFight(existing, Run, A, "1.5.2", 200, Meta(4), TwoPlayers(), new[] { A, B }, null, false, 1, out var o)!;
        Assert.Equal(RunStatsFile.Outcome.Appended, o);
        var fights = JsonNode.Parse(second)!["fights"]!.AsArray();
        Assert.Equal(before, fights[0]!.ToJsonString());
        Assert.Null(fights[1]!["log"]);
        Assert.Null(fights[1]!["log_dropped"]);
    }

    [Fact]
    public void LastWritten_IsTheFightJustAdded()
    {
        RunStatsFile.LastWritten = null;
        string first = RunStatsFile.AddFight(new List<JsonObject>(), Run, A, "1.6.0", 100, Meta(3), TwoPlayers(), new[] { A, B }, null, false, 1, out _)!;
        Assert.Equal((Run, 1, (int?)3), RunStatsFile.LastWritten);
        var existing = RunStatsFile.ParseFights(first, Run)!;
        string second = RunStatsFile.AddFight(existing, Run, A, "1.6.0", 200, Meta(5), TwoPlayers(), new[] { A, B }, null, false, 1, out _)!;
        Assert.Equal((Run, 2, (int?)5), RunStatsFile.LastWritten);

        // A fight replayed after a reload keeps the stored fight's index.
        RunStatsFile.AddFight(RunStatsFile.ParseFights(second, Run)!, Run, A, "1.6.0", 300, Meta(3, rounds: 9), TwoPlayers(), new[] { A, B }, null, false, 1, out var o);
        Assert.Equal(RunStatsFile.Outcome.Replaced, o);
        Assert.Equal((Run, 1, (int?)3), RunStatsFile.LastWritten);

        // An unknown floor is null.
        RunStatsFile.AddFight(new List<JsonObject>(), Run, A, "1.6.0", 100, new FightMeta(), TwoPlayers(), new[] { A, B }, null, false, 1, out _);
        Assert.Equal((Run, 1, (int?)null), RunStatsFile.LastWritten);
    }

    [Fact]
    public void LastWritten_IsTheIndexInTheFileWhenStoredIndexesAreRenumbered()
    {
        // Stored indexes out of order: Serialize renumbers by position, and LastWritten follows the file.
        var a = Fight(3); a["index"] = 5;
        var b = Fight(4); b["index"] = 2;
        string text = RunStatsFile.AddFight(new List<JsonObject> { a, b }, Run, A, "1.6.0", 100, Meta(8), TwoPlayers(), new[] { A, B }, null, false, 1, out _)!;
        var written = JsonNode.Parse(text)!["fights"]!.AsArray();
        Assert.Equal(3, (int)written[2]!["index"]!);
        Assert.Equal((Run, 3, (int?)8), RunStatsFile.LastWritten);
    }

    [Fact]
    public void LastWritten_IsClearedWhenNothingIsWritten()
    {
        var fights = new List<JsonObject>();
        for (int i = 1; i <= RunStatsFile.MaxFights; i++) RunStatsFile.Upsert(fights, Fight(i), 1);
        RunStatsFile.LastWritten = ("ffffffffffffffff", 1, 1);
        Assert.Null(RunStatsFile.AddFight(fights, Run, A, "1.6.0", 1, Meta(999), TwoPlayers(), new[] { A, B }, null, false, 1, out _));
        Assert.Null(RunStatsFile.LastWritten);
    }
}
