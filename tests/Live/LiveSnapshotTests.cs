using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using STS2_DamageCharts.Live;
using Xunit;

namespace STS2_DamageCharts.Tests.Live;

public class LiveSnapshotTests
{
    private const string Run = "0123456789abcdef";
    private const string Steam = "76561198000000001";

    private static LivePlayer Player(string steam = Steam, string character = "CHARACTER.SILENT", int hp = 50, int maxHp = 70,
        int block = 5, int dealt = 30, int taken = 10, int healed = 2, int blocked = 12, int absorbed = 7)
        => new(steam, character, hp, maxHp, block, dealt, taken, healed, blocked, absorbed);

    private static LiveEnemy Enemy(string id = "MONSTER.JAW_WORM", string name = "Jaw Worm", int hp = 40, int maxHp = 44,
        int block = 0, LiveIntent? intent = null) => new(id, name, hp, maxHp, block, intent);

    private static string Json(IReadOnlyList<LivePlayer>? players = null, IReadOnlyList<LiveEnemy>? enemies = null,
        string encounter = "Jaw Worm", int fight = 3, int floor = 5, int round = 2, string side = "player",
        string status = "active", int seq = 4, long writtenAt = 1_760_000_000)
        => LiveSnapshot.Json(Run, fight, encounter, floor, round, side, status, seq, writtenAt,
            players ?? new[] { Player() }, enemies ?? new[] { Enemy(intent: new LiveIntent("attack", 11, 1)) });

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Top_level_has_exactly_the_contract_keys_in_snake_case()
    {
        var root = Parse(Json());
        Assert.Equal(new[] { "v", "run_id", "fight", "encounter", "floor", "round", "side", "status", "seq", "written_at", "players", "enemies" },
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal(Run, root.GetProperty("run_id").GetString());
        Assert.Equal(3, root.GetProperty("fight").GetInt32());
        Assert.Equal("Jaw Worm", root.GetProperty("encounter").GetString());
        Assert.Equal(5, root.GetProperty("floor").GetInt32());
        Assert.Equal(2, root.GetProperty("round").GetInt32());
        Assert.Equal("player", root.GetProperty("side").GetString());
        Assert.Equal("active", root.GetProperty("status").GetString());
        Assert.Equal(4, root.GetProperty("seq").GetInt32());
        Assert.Equal(1_760_000_000L, root.GetProperty("written_at").GetInt64());
    }

    [Fact]
    public void Player_and_enemy_have_exactly_the_contract_keys_and_types()
    {
        var root = Parse(Json());
        var p = root.GetProperty("players")[0];
        Assert.Equal(new[] { "steam_id", "character", "hp", "max_hp", "block", "dealt", "taken", "healed", "blocked", "absorbed" },
            p.EnumerateObject().Select(x => x.Name));
        Assert.Equal(JsonValueKind.String, p.GetProperty("steam_id").ValueKind);
        Assert.Equal(Steam, p.GetProperty("steam_id").GetString());
        Assert.Equal("CHARACTER.SILENT", p.GetProperty("character").GetString());
        Assert.Equal(7, p.GetProperty("absorbed").GetInt32());
        Assert.Equal(12, p.GetProperty("blocked").GetInt32());

        var e = root.GetProperty("enemies")[0];
        Assert.Equal(new[] { "id", "name", "hp", "max_hp", "block", "intent" }, e.EnumerateObject().Select(x => x.Name));
        var i = e.GetProperty("intent");
        Assert.Equal(new[] { "kind", "damage", "hits" }, i.EnumerateObject().Select(x => x.Name));
        Assert.Equal("attack", i.GetProperty("kind").GetString());
        Assert.Equal(11, i.GetProperty("damage").GetInt32());
        Assert.Equal(1, i.GetProperty("hits").GetInt32());
    }

    [Fact]
    public void Names_drop_disallowed_characters_collapse_spaces_and_truncate()
    {
        Assert.Equal("Jaw Worm", LiveSnapshot.CleanName("  Jaw   Worm™ ", "X"));
        Assert.Equal("Cultist's Hex! Why?", LiveSnapshot.CleanName("Cultist's (Hex)! \"Why\"?", "X"));
        Assert.Equal(64, LiveSnapshot.CleanName(new string('a', 200), "X").Length);
        string cut = LiveSnapshot.CleanName(new string('a', 63) + " " + new string('b', 10), "X");
        Assert.Equal(63, cut.Length);   // no trailing space left by the cut
        Assert.Matches(new Regex("^[A-Za-z0-9_. '!?-]*$"), cut);
    }

    [Fact]
    public void An_empty_name_falls_back_to_the_model_id()
    {
        Assert.Equal("MONSTER.JAW_WORM", LiveSnapshot.CleanName("贺虫", "MONSTER.JAW_WORM"));
        var root = Parse(Json(enemies: new[] { Enemy(name: "   ") }));
        Assert.Equal("MONSTER.JAW_WORM", root.GetProperty("enemies")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Ids_are_uppercased_and_disallowed_characters_become_underscores()
    {
        Assert.Equal("MONSTER.JAW_WORM", LiveSnapshot.CleanId("Monster.jaw_worm"));
        Assert.Equal("A_B_C", LiveSnapshot.CleanId("a-b c"));
        Assert.Equal(64, LiveSnapshot.CleanId(new string('x', 100)).Length);
        Assert.Equal("UNKNOWN", LiveSnapshot.CleanId(""));
        var root = Parse(Json(players: new[] { Player(character: "character.the silent") }, enemies: new[] { Enemy(id: "jaw worm") }));
        Assert.Equal("CHARACTER.THE_SILENT", root.GetProperty("players")[0].GetProperty("character").GetString());
        Assert.Equal("JAW_WORM", root.GetProperty("enemies")[0].GetProperty("id").GetString());
    }

    [Fact]
    public void The_encounter_is_a_plain_name_like_the_server_checks_it()
    {
        // sts2-saves src/live.ts: encounter uses the name rule (isName), not the id rule.
        var root = Parse(Json(encounter: "Slime Boss (Elite) ★"));
        Assert.Equal("Slime Boss Elite", root.GetProperty("encounter").GetString());
        Assert.Equal("Unknown", Parse(Json(encounter: "")).GetProperty("encounter").GetString());
    }

    [Fact]
    public void Numbers_are_clamped_to_zero_through_99999()
    {
        var p = Player(hp: -5, maxHp: int.MaxValue, block: 100_000, dealt: int.MinValue, taken: 99_999, healed: 0, blocked: 123_456, absorbed: -1);
        var root = Parse(Json(players: new[] { p }, enemies: new[] { Enemy(hp: -1, maxHp: 1_000_000, block: -9, intent: new LiveIntent("attack", 500_000, -3)) },
            fight: -1, floor: 1_000_000, round: int.MaxValue, seq: -7, writtenAt: long.MaxValue));
        var pl = root.GetProperty("players")[0];
        Assert.Equal(0, pl.GetProperty("hp").GetInt32());
        Assert.Equal(99_999, pl.GetProperty("max_hp").GetInt32());
        Assert.Equal(99_999, pl.GetProperty("block").GetInt32());
        Assert.Equal(0, pl.GetProperty("dealt").GetInt32());
        Assert.Equal(99_999, pl.GetProperty("taken").GetInt32());
        Assert.Equal(99_999, pl.GetProperty("blocked").GetInt32());
        Assert.Equal(0, pl.GetProperty("absorbed").GetInt32());
        var en = root.GetProperty("enemies")[0];
        Assert.Equal(0, en.GetProperty("hp").GetInt32());
        Assert.Equal(99_999, en.GetProperty("max_hp").GetInt32());
        Assert.Equal(0, en.GetProperty("block").GetInt32());
        Assert.Equal(99_999, en.GetProperty("intent").GetProperty("damage").GetInt32());
        Assert.Equal(0, en.GetProperty("intent").GetProperty("hits").GetInt32());
        Assert.Equal(0, root.GetProperty("fight").GetInt32());
        Assert.Equal(99_999, root.GetProperty("floor").GetInt32());
        Assert.Equal(99_999, root.GetProperty("round").GetInt32());
        Assert.Equal(0, root.GetProperty("seq").GetInt32());
        Assert.Equal(1L << 31, root.GetProperty("written_at").GetInt64());
    }

    [Fact]
    public void A_missing_intent_or_an_attack_with_no_damage_is_null()
    {
        Assert.Equal(JsonValueKind.Null, Parse(Json(enemies: new[] { Enemy(intent: null) })).GetProperty("enemies")[0].GetProperty("intent").ValueKind);
        Assert.Equal(JsonValueKind.Null,
            Parse(Json(enemies: new[] { Enemy(intent: new LiveIntent("attack", 0, 2)) })).GetProperty("enemies")[0].GetProperty("intent").ValueKind);
    }

    [Fact]
    public void Non_attack_intents_keep_their_kind_with_zero_damage()
    {
        foreach (var kind in new[] { "buff", "debuff", "defend", "unknown" })
        {
            var i = Parse(Json(enemies: new[] { Enemy(intent: new LiveIntent(kind, 0, 0)) })).GetProperty("enemies")[0].GetProperty("intent");
            Assert.Equal(kind, i.GetProperty("kind").GetString());
            Assert.Equal(0, i.GetProperty("damage").GetInt32());
        }
    }

    [Fact]
    public void An_unrecognised_intent_kind_becomes_unknown()
    {
        var i = Parse(Json(enemies: new[] { Enemy(intent: new LiveIntent("Stun", 0, 0)) })).GetProperty("enemies")[0].GetProperty("intent");
        Assert.Equal("unknown", i.GetProperty("kind").GetString());
    }

    [Fact]
    public void Nine_players_and_eleven_enemies_are_cut_to_the_caps()
    {
        var players = Enumerable.Range(0, 9).Select(i => Player(steam: "7656119800000000" + i)).ToArray();
        var enemies = Enumerable.Range(0, 11).Select(i => Enemy(id: "M" + i)).ToArray();
        var root = Parse(Json(players: players, enemies: enemies));
        Assert.Equal(8, root.GetProperty("players").GetArrayLength());
        Assert.Equal(10, root.GetProperty("enemies").GetArrayLength());
        Assert.Equal("M9", root.GetProperty("enemies")[9].GetProperty("id").GetString());
    }

    [Fact]
    public void A_player_without_a_17_digit_steam_id_is_left_out()
    {
        var root = Parse(Json(players: new[] { Player(steam: "1"), Player(steam: "7656119800000000x"), Player() }));
        Assert.Equal(1, root.GetProperty("players").GetArrayLength());
    }

    [Fact]
    public void Odd_data_never_throws_nulls_and_nonsense_become_safe_values()
    {
        var players = new[] { new LivePlayer(Steam, null!, 1, 1, 0, 0, 0, 0, 0, 0), null! };
        var enemies = new[] { new LiveEnemy(null!, null!, 1, 1, 0, new LiveIntent(null!, 5, 1)), null! };
        string json = LiveSnapshot.Json(Run, 1, null!, 1, 1, "player", "active", 1, 1, players, enemies);
        var root = Parse(json);
        Assert.Equal("UNKNOWN", root.GetProperty("players")[0].GetProperty("character").GetString());
        Assert.Equal(1, root.GetProperty("players").GetArrayLength());
        var e = root.GetProperty("enemies")[0];
        Assert.Equal("UNKNOWN", e.GetProperty("id").GetString());
        Assert.Equal("UNKNOWN", e.GetProperty("name").GetString());
        Assert.Equal("unknown", e.GetProperty("intent").GetProperty("kind").GetString());
        Assert.Equal(1, root.GetProperty("enemies").GetArrayLength());
    }

    [Fact]
    public void Null_lists_are_treated_as_empty()
    {
        var root = Parse(LiveSnapshot.Json(Run, 1, "x", 1, 1, "enemy", "ended", 1, 1, null!, null!));
        Assert.Equal(0, root.GetProperty("players").GetArrayLength());
        Assert.Equal(0, root.GetProperty("enemies").GetArrayLength());
    }

    [Theory]
    [InlineData("0123456789ABCDEF", "side", "active")]
    [InlineData("short", "player", "active")]
    [InlineData("0123456789abcdef", "sideways", "active")]
    [InlineData("0123456789abcdef", "player", "paused")]
    public void An_identity_the_server_would_reject_is_refused_loudly(string run, string side, string status)
    {
        if (side == "side") side = "player";
        Assert.Throws<ArgumentException>(() => LiveSnapshot.Json(run, 1, "x", 1, 1, side, status, 1, 1,
            Array.Empty<LivePlayer>(), Array.Empty<LiveEnemy>()));
    }

    [Fact]
    public void The_largest_state_stays_under_8_kb_and_passes_the_server_rules()
    {
        string longName = new string('W', 64);
        var players = Enumerable.Range(0, 8).Select(i => Player(steam: "7656119800000000" + i, character: new string('C', 64),
            hp: 99_999, maxHp: 99_999, block: 99_999, dealt: 99_999, taken: 99_999, healed: 99_999, blocked: 99_999, absorbed: 99_999)).ToArray();
        var enemies = Enumerable.Range(0, 10).Select(i => Enemy(id: new string('I', 64), name: longName, hp: 99_999, maxHp: 99_999,
            block: 99_999, intent: new LiveIntent("debuff", 99_999, 99_999))).ToArray();
        string json = Json(players: players, enemies: enemies, encounter: longName);
        Assert.True(Encoding.UTF8.GetByteCount(json) < 8 * 1024);
        ServerRules.AssertValid(json);
    }

    [Fact]
    public void Normal_output_passes_the_server_rules_and_has_no_em_dash()
    {
        string json = Json();
        ServerRules.AssertValid(json);
        Assert.DoesNotContain('\u2014', json);
    }
}

// A copy of the checks in sts2-saves src/live.ts parseLiveState, so the mod's output is validated the way the server will.
internal static class ServerRules
{
    private static readonly Regex RunId = new("^[0-9a-f]{16}$");
    private static readonly Regex SteamId = new("^[0-9]{17}$");
    private static readonly Regex Id = new("^[A-Z0-9_.]{1,64}$");
    private static readonly Regex Name = new("^[A-Za-z0-9_. '!?-]*$");

    private static void Keys(JsonElement o, params string[] keys)
    {
        var names = o.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.True(names.SetEquals(keys), "keys: " + string.Join(",", names));
    }

    private static void Num(JsonElement e, long max = 99_999)
    {
        Assert.Equal(JsonValueKind.Number, e.ValueKind);
        Assert.True(e.TryGetInt64(out long v) && v >= 0 && v <= max, $"number {e}");
    }

    public static void AssertValid(string json)
    {
        var root = JsonDocument.Parse(json).RootElement;
        Keys(root, "v", "run_id", "fight", "encounter", "floor", "round", "side", "status", "seq", "written_at", "players", "enemies");
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Matches(RunId, root.GetProperty("run_id").GetString()!);
        string enc = root.GetProperty("encounter").GetString()!;
        Assert.True(enc.Length <= 64 && Name.IsMatch(enc));
        foreach (var k in new[] { "fight", "floor", "round", "seq" }) Num(root.GetProperty(k));
        Num(root.GetProperty("written_at"), 1L << 31);
        Assert.Contains(root.GetProperty("side").GetString(), new[] { "player", "enemy" });
        Assert.Contains(root.GetProperty("status").GetString(), new[] { "active", "ended" });
        var players = root.GetProperty("players");
        Assert.True(players.GetArrayLength() <= 8);
        foreach (var p in players.EnumerateArray())
        {
            Keys(p, "steam_id", "character", "hp", "max_hp", "block", "dealt", "taken", "healed", "blocked", "absorbed");
            Assert.Matches(SteamId, p.GetProperty("steam_id").GetString()!);
            Assert.Matches(Id, p.GetProperty("character").GetString()!);
            foreach (var k in new[] { "hp", "max_hp", "block", "dealt", "taken", "healed", "blocked", "absorbed" }) Num(p.GetProperty(k));
        }
        var enemies = root.GetProperty("enemies");
        Assert.True(enemies.GetArrayLength() <= 10);
        foreach (var e in enemies.EnumerateArray())
        {
            Keys(e, "id", "name", "hp", "max_hp", "block", "intent");
            Assert.Matches(Id, e.GetProperty("id").GetString()!);
            string n = e.GetProperty("name").GetString()!;
            Assert.True(n.Length <= 64 && Name.IsMatch(n));
            foreach (var k in new[] { "hp", "max_hp", "block" }) Num(e.GetProperty(k));
            var i = e.GetProperty("intent");
            if (i.ValueKind == JsonValueKind.Null) continue;
            Keys(i, "kind", "damage", "hits");
            Assert.Contains(i.GetProperty("kind").GetString(), new[] { "attack", "buff", "debuff", "defend", "unknown" });
            Num(i.GetProperty("damage")); Num(i.GetProperty("hits"));
        }
    }
}
