using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using STS2_DamageCharts.Forecast;
using STS2_DamageCharts.Tracking;

namespace STS2_DamageCharts.Stats;

// The per-run damage stats file (format v1), shared with STS2 Save Sync and the save library server. The
// contract lives in sts2-saves docs/specs/2026-11-11-damage-stats-format.md. Pure: no Godot or game
// references, so the tests compile it directly; the mod does the file I/O.
//
// One file per run, rewritten after each finished combat: earlier fights are kept as they were read from
// disk (so a continued save keeps its history) and the new fight is appended, or replaces the fight it
// repeats (the same floor and the same per-floor ordinal, i.e. a fight replayed after reloading a save).
internal static class RunStatsFile
{
    public const int Version = 1;
    public const int MaxFights = 200;
    public const int MaxPlayers = 8;
    public const int MaxSources = 50;
    public const int MaxString = 80;
    public const int MaxBytes = 1_000_000; // the spec's 1 MB, read conservatively as decimal
    public const long MaxAmount = 1_000_000_000; // the server rejects amounts above 1e9

    // The only keys the server accepts at each level (any other key is a 400).
    private static readonly string[] FightKeys = { "index", "floor", "encounter", "kind", "rounds", "players" };
    private static readonly string[] PlayerKeys = { "steam_id", "dealt", "taken", "healed", "block", "absorbed", "potions" };
    private static readonly string[] ListKeys = { "total", "sources" };
    private static readonly string[] SourceKeys = { "name", "id", "amount", "hits" };

    public static long Amount(long v) => Math.Clamp(v, 0, MaxAmount);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        // A local file read by our own app and server: keep "Strike+" and accented names readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public enum Outcome { Appended, Replaced, Full, TooLarge }

    // monster | elite | boss | event | unknown. A combat started by an event is "event" whatever its encounter.
    public static string KindName(FightMeta? meta)
    {
        if (meta == null) return "unknown";
        if (meta.FromEvent) return "event";
        return meta.Kind switch
        {
            FightKind.Normal => "monster",
            FightKind.Elite => "elite",
            FightKind.Boss => "boss",
            _ => "unknown",
        };
    }

    // The "mod_version" to write: the installed manifest's version, else the DLL's own version (stamped from the
    // manifest at build time), else the build tag. Never empty, since the server requires the field.
    public static string ModVersion(string? manifest, string? assembly, string buildTag)
    {
        foreach (var v in new[] { manifest, assembly })
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return buildTag;
    }

    // At most `max` UTF-16 units (the server's string length), never splitting a surrogate pair.
    public static string? Clip(string? s, int max = MaxString)
    {
        if (s == null) return null;
        s = s.Trim();
        if (s.Length <= max) return s;
        int n = max;
        if (n > 0 && char.IsHighSurrogate(s[n - 1])) n--;
        return s.Substring(0, n);
    }

    // One fight's entry, without "index" (Upsert assigns it). `steamIds[slot]` names each player slot of
    // `perPlayer`; a slot without an id or data is left out. Sources are capped at `maxSources` per list
    // (largest first); totals always cover every source.
    public static JsonObject BuildFight(FightMeta? meta, IReadOnlyList<PlayerSources?> perPlayer,
        IReadOnlyList<string?> steamIds, int maxSources = MaxSources)
    {
        var players = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int s = 0; s < perPlayer.Count && players.Count < MaxPlayers; s++)
        {
            var ps = perPlayer[s];
            string? sid = s < steamIds.Count ? steamIds[s] : null;
            if (ps == null || string.IsNullOrEmpty(sid) || !seen.Add(sid)) continue; // one entry per steam id
            var potions = new JsonArray();
            foreach (var p in ps.Potions)
                for (long i = 0; i < p.Total && potions.Count < MaxSources; i++) potions.Add(JsonValue.Create(Clip(p.Name)));
            var player = new JsonObject
            {
                ["steam_id"] = sid,
                ["dealt"] = SourceList(ps.DealtTotal, ps.Dealt, maxSources),
                ["taken"] = SourceList(ps.TakenTotal, ps.Taken, maxSources),
                ["healed"] = Amount(ps.HealTotal),
                ["block"] = Amount(ps.BlockTotal),
                ["potions"] = potions,
            };
            // Optional in the format (sts2-saves accepts it since Nov 2026); only written when a pet took hits.
            if (ps.AbsorbedTotal > 0) player["absorbed"] = Amount(ps.AbsorbedTotal);
            players.Add(player);
        }
        return new JsonObject
        {
            ["floor"] = meta != null && meta.Floor > 0 ? meta.Floor : null,
            ["encounter"] = Clip(string.IsNullOrWhiteSpace(meta?.Name) ? null : meta!.Name),
            ["kind"] = KindName(meta),
            ["rounds"] = meta != null && meta.Rounds > 0 ? meta.Rounds : null,
            ["players"] = players,
        };
    }

    private static JsonObject SourceList(long total, IReadOnlyList<SourceEntry> entries, int max)
    {
        // Largest first; equal amounts keep their order.
        var sorted = new List<(SourceEntry E, int I)>(entries.Count);
        for (int i = 0; i < entries.Count; i++) if (entries[i].Total > 0) sorted.Add((entries[i], i));
        sorted.Sort((a, b) => a.E.Total != b.E.Total ? b.E.Total.CompareTo(a.E.Total) : a.I.CompareTo(b.I));
        var list = new JsonArray();
        for (int i = 0; i < sorted.Count && i < max; i++)
        {
            var e = sorted[i].E;
            var o = new JsonObject
            {
                ["name"] = Clip(e.Name),
                ["id"] = Clip(e.Id),
                ["amount"] = Amount(e.Total),
            };
            if (e.Hits > 0) o["hits"] = Amount(e.Hits); // unknown hit counts are omitted, never guessed
            list.Add(o);
        }
        return new JsonObject { ["total"] = Amount(total), ["sources"] = list };
    }

    // The fights already in a v1 file for `runId`, in file order; null when the text is not such a file
    // (unparseable, another version, or another run's file).
    public static List<JsonObject>? ParseFights(string json, string runId)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return null;
            if (root["v"] is not JsonValue v || !v.TryGetValue(out int ver) || ver != Version) return null;
            if (root["run_id"] is not JsonValue r || !r.TryGetValue(out string? rid) || rid != runId) return null;
            if (root["fights"] is not JsonArray arr) return null;
            var fights = new List<JsonObject>(arr.Count);
            foreach (var f in arr)
                if (f is JsonObject o && Clean(o, FightKeys) is { } clean) fights.Add(clean);
            return fights;
        }
        catch { return null; }
    }

    // A copy holding only the spec's keys (recursively), so a stray key in an older or hand-edited file can't
    // get the whole upload rejected. Null for a fight without its required players array.
    private static JsonObject? Clean(JsonObject fight, string[] keys)
    {
        var o = Keep(fight, keys);
        if (o["players"] is not JsonArray players) return null;
        var cleanPlayers = new JsonArray();
        foreach (var p in players)
        {
            if (p is not JsonObject po) continue;
            var cp = Keep(po, PlayerKeys);
            foreach (var side in new[] { "dealt", "taken" })
            {
                if (cp[side] is not JsonObject list) continue;
                var cl = Keep(list, ListKeys);
                if (cl["sources"] is JsonArray srcs)
                {
                    var cs = new JsonArray();
                    foreach (var e in srcs) if (e is JsonObject eo) cs.Add(Keep(eo, SourceKeys));
                    cl["sources"] = cs;
                }
                cp[side] = cl;
            }
            cleanPlayers.Add(cp);
        }
        o["players"] = cleanPlayers;
        return o;
    }

    private static JsonObject Keep(JsonObject from, string[] keys)
    {
        var o = new JsonObject();
        foreach (var k in keys) if (from.TryGetPropertyValue(k, out var v)) o[k] = v?.DeepClone();
        return o;
    }

    // Built nodes hold CLR ints, parsed ones hold JSON numbers: TryGetValue<int> reads both.
    private static int? IntOf(JsonNode? n) => n is JsonValue v && v.TryGetValue(out int i) ? i : null;

    // Add `fight` to `fights`. With a known floor, the `floorOrdinal`-th fight on that floor (1-based, counted
    // this session) already in the file is the same fight played again after a reload: it is replaced and
    // keeps its index. Otherwise the fight is appended with the next index, unless the file is full.
    public static Outcome Upsert(List<JsonObject> fights, JsonObject fight, int floorOrdinal)
    {
        int? floor = IntOf(fight["floor"]);
        if (floor is int f && floorOrdinal > 0)
        {
            int seen = 0;
            for (int i = 0; i < fights.Count; i++)
            {
                if (IntOf(fights[i]["floor"]) != f) continue;
                if (++seen != floorOrdinal) continue;
                var replaced = WithIndex(fight, IntOf(fights[i]["index"]) ?? i + 1);
                fights[i] = replaced;
                return Outcome.Replaced;
            }
        }
        if (fights.Count >= MaxFights) return Outcome.Full;
        int next = 0;
        foreach (var e in fights) next = Math.Max(next, IntOf(e["index"]) ?? 0);
        next = Math.Max(next, fights.Count) + 1;
        fights.Add(WithIndex(fight, next));
        return Outcome.Appended;
    }

    // "index" first, as in the spec.
    private static JsonObject WithIndex(JsonObject fight, int index)
    {
        var o = new JsonObject { ["index"] = index };
        foreach (var kv in fight) if (kv.Key != "index") o[kv.Key] = kv.Value?.DeepClone();
        return o;
    }

    public static string Serialize(string runId, string recordedBy, string modVersion, long updated, List<JsonObject> fights)
    {
        // "index" must start at 1 and strictly increase: keep the stored numbers when they do, else renumber.
        bool ordered = true;
        int last = 0;
        foreach (var f in fights)
        {
            int? i = IntOf(f["index"]);
            if (i is not int n || n <= last) { ordered = false; break; }
            last = n;
        }
        var arr = new JsonArray();
        for (int k = 0; k < fights.Count; k++)
            arr.Add(ordered ? fights[k].DeepClone() : WithIndex(fights[k], k + 1));
        var root = new JsonObject
        {
            ["v"] = Version,
            ["run_id"] = runId,
            ["recorded_by"] = recordedBy,
        };
        root["mod_version"] = Clip(modVersion); // required by the server: a file without it is rejected
        root["updated"] = updated;
        root["fights"] = arr;
        return root.ToJsonString(Json);
    }

    // The whole update: `existing` (the file's current fights, from ParseFights, or empty) gains this fight,
    // and the new file text comes back; null when nothing should be written (the file is full, or even a
    // fight without source lists would push it past the size cap). The new fight's source lists shrink as
    // needed to fit; earlier fights are never rewritten. `existing` is left unchanged.
    public static string? AddFight(List<JsonObject> existing, string runId, string recordedBy, string modVersion,
        long updated, FightMeta? meta, IReadOnlyList<PlayerSources?> perPlayer, IReadOnlyList<string?> steamIds,
        int floorOrdinal, out Outcome outcome)
    {
        outcome = Outcome.Full;
        foreach (int maxSources in new[] { MaxSources, 10, 0 })
        {
            var fights = new List<JsonObject>(existing);
            outcome = Upsert(fights, BuildFight(meta, perPlayer, steamIds, maxSources), floorOrdinal);
            if (outcome == Outcome.Full) return null;
            string text = Serialize(runId, recordedBy, modVersion, updated, fights);
            if (Encoding.UTF8.GetByteCount(text) <= MaxBytes) return text;
        }
        outcome = Outcome.TooLarge;
        return null;
    }
}
