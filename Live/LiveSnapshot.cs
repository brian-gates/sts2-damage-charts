using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace STS2_DamageCharts.Live;

// One player's row of the live state. SteamId is the player's SteamID64; Blocked is block gained this fight and
// Absorbed is the damage that block stopped.
internal sealed record LivePlayer(string SteamId, string Character, int Hp, int MaxHp, int Block, int Dealt, int Taken,
    int Healed, int Blocked, int Absorbed);

// An enemy's telegraphed move: Kind is attack, buff, debuff, defend or unknown; Damage is per hit.
internal sealed record LiveIntent(string Kind, int Damage, int Hits);

internal sealed record LiveEnemy(string Id, string Name, int Hp, int MaxHp, int Block, LiveIntent? Intent);

// Everything about the fight that the file shows, before the writer adds status, seq and time.
internal sealed record LiveInput(string RunId, int Fight, string Encounter, int Floor, int Round, string Side,
    IReadOnlyList<LivePlayer> Players, IReadOnlyList<LiveEnemy> Enemies);

// Pure (no Godot or game references): plain data in, the live.json text out. The shape and limits are the
// server's (sts2-saves src/live.ts parseLiveState), which rejects the whole state on any violation, so this
// clamps and cleans every value rather than trusting the game.
internal static class LiveSnapshot
{
    public const int MaxPlayers = 8;
    public const int MaxEnemies = 10;
    public const int MaxNumber = 99_999;
    public const int MaxName = 64;
    private const long MaxWrittenAt = 1L << 31;
    private static readonly string[] IntentKinds = { "attack", "buff", "debuff", "defend", "unknown" };

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>The live.json text. Throws ArgumentException for a run id, side or status the server would reject
    /// (the caller's identity is wrong, not just odd data); every other oddity is cleaned or dropped.</summary>
    public static string Json(string runId, int fight, string encounter, int floor, int round, string side, string status,
        int seq, long writtenAt, IReadOnlyList<LivePlayer> players, IReadOnlyList<LiveEnemy> enemies)
    {
        if (!IsRunId(runId)) throw new ArgumentException("run id must be 16 lowercase hex characters", nameof(runId));
        if (side != "player" && side != "enemy") throw new ArgumentException("side must be player or enemy", nameof(side));
        if (status != "active" && status != "ended") throw new ArgumentException("status must be active or ended", nameof(status));

        var ps = new JsonArray();
        if (players != null)
            foreach (var p in players)
            {
                if (ps.Count >= MaxPlayers) break;
                if (p == null || !IsSteamId(p.SteamId)) continue;
                ps.Add(new JsonObject
                {
                    ["steam_id"] = p.SteamId,
                    ["character"] = CleanId(p.Character),
                    ["hp"] = Num(p.Hp),
                    ["max_hp"] = Num(p.MaxHp),
                    ["block"] = Num(p.Block),
                    ["dealt"] = Num(p.Dealt),
                    ["taken"] = Num(p.Taken),
                    ["healed"] = Num(p.Healed),
                    ["blocked"] = Num(p.Blocked),
                    ["absorbed"] = Num(p.Absorbed),
                });
            }

        var es = new JsonArray();
        if (enemies != null)
            foreach (var e in enemies)
            {
                if (es.Count >= MaxEnemies) break;
                if (e == null) continue;
                string id = CleanId(e.Id);
                es.Add(new JsonObject
                {
                    ["id"] = id,
                    ["name"] = CleanName(e.Name, id),
                    ["hp"] = Num(e.Hp),
                    ["max_hp"] = Num(e.MaxHp),
                    ["block"] = Num(e.Block),
                    ["intent"] = IntentNode(e.Intent),
                });
            }

        var root = new JsonObject
        {
            ["v"] = 1,
            ["run_id"] = runId,
            ["fight"] = Num(fight),
            ["encounter"] = CleanName(encounter, "Unknown"),
            ["floor"] = Num(floor),
            ["round"] = Num(round),
            ["side"] = side,
            ["status"] = status,
            ["seq"] = Num(seq),
            ["written_at"] = Math.Clamp(writtenAt, 0L, MaxWrittenAt),
            ["players"] = ps,
            ["enemies"] = es,
        };
        return root.ToJsonString(Compact);
    }

    // An attack with no damage has nothing to show (a modified 0, or unreadable), so it reads as no intent.
    private static JsonNode? IntentNode(LiveIntent? i)
    {
        if (i == null) return null;
        string kind = Array.IndexOf(IntentKinds, i.Kind) >= 0 ? i.Kind : "unknown";
        if (kind == "attack" && Num(i.Damage) == 0) return null;
        return new JsonObject { ["kind"] = kind, ["damage"] = Num(i.Damage), ["hits"] = Num(i.Hits) };
    }

    private static int Num(int v) => Math.Clamp(v, 0, MaxNumber);

    /// <summary>A display name: only the server's allowed characters (letters, digits, "_. '!?-"), spaces collapsed
    /// and trimmed, at most 64 long. Falls back to <paramref name="fallback"/> when nothing is left.</summary>
    public static string CleanName(string? name, string fallback)
    {
        var sb = new StringBuilder();
        bool space = false;
        foreach (char c in name ?? "")
        {
            if (c == ' ') { space = sb.Length > 0; continue; }
            if (!IsNameChar(c)) continue;
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        if (sb.Length > MaxName) sb.Length = MaxName;
        string s = sb.ToString().TrimEnd();
        return s.Length > 0 ? s : fallback;
    }

    /// <summary>A model id as the server wants it: uppercase, anything outside A-Z 0-9 "_." replaced by "_", 1 to 64 long.</summary>
    public static string CleanId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "UNKNOWN";
        var sb = new StringBuilder(Math.Min(id.Length, MaxName));
        foreach (char raw in id.ToUpperInvariant())
        {
            if (sb.Length >= MaxName) break;
            sb.Append(IsIdChar(raw) ? raw : '_');
        }
        return sb.ToString();
    }

    private static bool IsIdChar(char c) => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '.';

    private static bool IsNameChar(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '.' or '\'' or '!' or '?' or '-';

    private static bool IsRunId(string? s)
    {
        if (s == null || s.Length != 16) return false;
        foreach (char c in s) if (!(c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        return true;
    }

    private static bool IsSteamId(string? s)
    {
        if (s == null || s.Length != 17) return false;
        foreach (char c in s) if (c < '0' || c > '9') return false;
        return true;
    }
}
