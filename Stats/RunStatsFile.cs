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
    public const int MaxLogHits = 600;
    public const int MaxLogNames = 600;
    public const int MaxRound = 10000;
    public const int MaxBytes = 1_000_000; // the spec's 1 MB, read conservatively as decimal
    public const long MaxAmount = 1_000_000_000; // the server rejects amounts above 1e9
    public const int MaxMisses = 20;      // forecast misses per file, over all fights
    public const int MaxMissItems = 20;   // items per miss
    public const int MaxMissKind = 24;    // a miss item's kind string

    // The only keys the server accepts at each level (any other key is a 400).
    private static readonly string[] FightKeys = { "index", "floor", "encounter", "kind", "rounds", "players", "log", "log_dropped", "forecast_misses" };
    private static readonly string[] PlayerKeys = { "steam_id", "dealt", "taken", "healed", "block", "absorbed", "buffered", "potions" };
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
    // (largest first); totals always cover every source. `hits` is the fight's combat log (null when unknown):
    // it is written as "log" when `withLog`, else the fight says "log_dropped" so readers know one existed.
    // `misses` (the local player's forecast misses) are written as "forecast_misses", at most MaxMisses; none,
    // no key. AddFight trims them further so the whole file stays within MaxMisses.
    public static JsonObject BuildFight(FightMeta? meta, IReadOnlyList<PlayerSources?> perPlayer,
        IReadOnlyList<string?> steamIds, IReadOnlyList<Hit>? hits = null, bool hitsTruncated = false,
        int maxSources = MaxSources, bool withLog = true, IReadOnlyList<ForecastMiss>? misses = null)
    {
        var players = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var logSlot = new Dictionary<int, int>(); // player slot -> index in "players" (only written players)
        var logPlayers = new List<string>();
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
            // Optional since mod 1.6; like absorbed, only written when a Buffer saved something.
            if (ps.BufferedTotal > 0) player["buffered"] = Amount(ps.BufferedTotal);
            logSlot[s] = players.Count;
            logPlayers.Add(sid);
            players.Add(player);
        }
        var fight = new JsonObject
        {
            ["floor"] = meta != null && meta.Floor > 0 ? meta.Floor : null,
            ["encounter"] = Clip(string.IsNullOrWhiteSpace(meta?.Name) ? null : meta!.Name),
            ["kind"] = KindName(meta),
            ["rounds"] = meta != null && meta.Rounds > 0 ? meta.Rounds : null,
            ["players"] = players,
        };
        if (hits != null)
        {
            if (!withLog) fight["log_dropped"] = true;
            else fight["log"] = BuildLog(hits, hitsTruncated, logSlot, logPlayers);
        }
        if (misses != null && misses.Count > 0)
        {
            var arr = new JsonArray();
            for (int i = 0; i < misses.Count && i < MaxMisses; i++) arr.Add(MissJson(misses[i]));
            fight["forecast_misses"] = arr;
        }
        return fight;
    }

    // { round, min, max, actual, hidden, block, buffer, intangible, osty, wuppy, items }: every field is always
    // known to the forecast, so none is omitted (and none is ever null).
    private static JsonObject MissJson(ForecastMiss m)
    {
        var items = new JsonArray();
        for (int i = 0; i < m.Items.Count && i < MaxMissItems; i++)
        {
            var it = m.Items[i];
            items.Add(new JsonObject
            {
                ["kind"] = Clip(string.IsNullOrWhiteSpace(it.Kind) ? "other" : it.Kind, MaxMissKind),
                ["label"] = Clip(it.Label) ?? "",
                ["hits"] = Amount(it.Hits),
                ["per_hit"] = Amount(it.PerHit),
                ["conditional"] = it.Conditional,
            });
        }
        long min = Amount(m.Min);
        return new JsonObject
        {
            ["round"] = Math.Clamp(m.Round, 0, MaxRound),
            ["min"] = min,
            ["max"] = Math.Max(min, Amount(m.Max)),
            ["actual"] = Amount(m.Actual),
            ["hidden"] = m.Hidden,
            ["block"] = Amount(m.Block),
            ["buffer"] = Amount(m.Buffer),
            ["intangible"] = m.Intangible,
            ["osty"] = Amount(m.Osty),
            ["wuppy"] = Amount(m.Wuppy),
            ["items"] = items,
        };
    }

    private static int MissCount(JsonObject fight) => fight["forecast_misses"] is JsonArray a ? a.Count : 0;

    // Keep the file within MaxMisses: the fight at `at` keeps only what the other fights leave room for.
    // Other fights are never touched.
    private static void CapMisses(List<JsonObject> fights, int at)
    {
        int others = 0;
        for (int i = 0; i < fights.Count; i++) if (i != at) others += MissCount(fights[i]);
        int room = Math.Max(0, MaxMisses - others);
        var f = fights[at];
        if (f["forecast_misses"] is not JsonArray arr || arr.Count <= room) return;
        if (room == 0) { f.Remove("forecast_misses"); return; }
        var kept = new JsonArray();
        for (int i = 0; i < room; i++) kept.Add(arr[i]!.DeepClone());
        f["forecast_misses"] = kept;
    }

    // The fight's hit log in the shared compact form. Only hits of written players are kept (the server rejects a
    // log naming a player the fight lacks), and a player side that isn't written becomes -1. A player side is a
    // ref when it is a player other than the hit's own player; otherwise the side is the named thing that did
    // or received it (a card, power or monster), as for a dealt hit's source. A healed hit has no target: the
    // healed player is already the hit's player. A buffered hit is written like a taken one (the attacker, or -1
    // when unknown, and the player ref); its amount is the HP the Buffer saved. With no hit left the log is empty (no players or names), so a fight that
    // simply had no hits still reads as logged.
    private static JsonObject BuildLog(IReadOnlyList<Hit> hits, bool truncated,
        Dictionary<int, int> logSlot, List<string> logPlayers)
    {
        var names = new List<string>();
        var ids = new List<string?>();
        var nameIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        int Name(string? raw, string? id)
        {
            string? n = Clip(raw);
            if (string.IsNullOrEmpty(n)) return -1;
            if (nameIndex.TryGetValue(n, out int at))
            {
                ids[at] ??= Clip(id); // the first id seen for the name (a null one is filled by a later id)
                return at;
            }
            if (names.Count >= MaxLogNames) return -1;
            nameIndex[n] = names.Count;
            names.Add(n);
            ids.Add(Clip(id));
            return names.Count - 1;
        }
        int Ref(int slot) => logSlot.TryGetValue(slot, out int i) ? -(i + 2) : -1;

        var rows = new JsonArray();
        for (int i = 0; i < hits.Count; i++)
        {
            if (i >= MaxLogHits) { truncated = true; break; }
            var h = hits[i];
            if (!logSlot.TryGetValue(h.Slot, out int player)) continue;
            int source = h.SourceSlot >= 0 && h.SourceSlot != h.Slot ? Ref(h.SourceSlot) : Name(h.Source, h.SourceId);
            int target = h.Kind == HitKind.Healed ? -1 : h.TargetSlot >= 0 ? Ref(h.TargetSlot) : Name(h.Target, null);
            long amount = h.Kind == HitKind.Potion ? -1 : Amount(h.Amount); // -1 (no amount) is for potion lines only
            rows.Add(new JsonArray((int)Math.Clamp(h.Round, 0, MaxRound), (int)h.Kind, player, source, target, amount));
        }
        var namesArr = new JsonArray();
        foreach (var n in names) namesArr.Add(n);
        var idsArr = new JsonArray();
        foreach (var id in ids) idsArr.Add(id);
        var players = new JsonArray();
        if (rows.Count > 0) foreach (var sid in logPlayers) players.Add(sid);
        return new JsonObject
        {
            ["players"] = players,
            ["names"] = namesArr,
            ["ids"] = idsArr,
            ["hits"] = rows,
            ["truncated"] = truncated,
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
    public static Outcome Upsert(List<JsonObject> fights, JsonObject fight, int floorOrdinal) =>
        Upsert(fights, fight, floorOrdinal, out _);

    // The index a fight on `floor` (0 = unknown) would get if it were added now: what AddFight would write for it,
    // by the same Upsert and index rules. Null when the file is full. `fights` is left unchanged. (A fight too large
    // for the file is not predicted.) The live state uses it as its "fight" number.
    public static int? NextIndex(List<JsonObject> fights, int floor, int floorOrdinal)
    {
        var copy = new List<JsonObject>(fights);
        var probe = new JsonObject { ["floor"] = floor > 0 ? floor : null };
        if (Upsert(copy, probe, floorOrdinal, out int at) == Outcome.Full) return null;
        return WrittenIndex(copy, at);
    }

    // `at`: where the fight went in `fights` (-1 when the file is full).
    private static Outcome Upsert(List<JsonObject> fights, JsonObject fight, int floorOrdinal, out int at)
    {
        at = -1;
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
                at = i;
                return Outcome.Replaced;
            }
        }
        if (fights.Count >= MaxFights) return Outcome.Full;
        int next = 0;
        foreach (var e in fights) next = Math.Max(next, IntOf(e["index"]) ?? 0);
        next = Math.Max(next, fights.Count) + 1;
        fights.Add(WithIndex(fight, next));
        at = fights.Count - 1;
        return Outcome.Appended;
    }

    // "index" first, as in the spec.
    private static JsonObject WithIndex(JsonObject fight, int index)
    {
        var o = new JsonObject { ["index"] = index };
        foreach (var kv in fight) if (kv.Key != "index") o[kv.Key] = kv.Value?.DeepClone();
        return o;
    }

    // "index" must start at 1 and strictly increase: Serialize keeps the stored numbers when they do, else
    // renumbers by position. The index fight `k` gets in the file.
    private static bool IndexesOrdered(List<JsonObject> fights)
    {
        int last = 0;
        foreach (var f in fights)
        {
            int? i = IntOf(f["index"]);
            if (i is not int n || n <= last) return false;
            last = n;
        }
        return true;
    }

    private static int WrittenIndex(List<JsonObject> fights, int k) =>
        IndexesOrdered(fights) && IntOf(fights[k]["index"]) is int i ? i : k + 1;

    // `gameVersion`: the game build, written as "game_version" when known (optional in the format).
    public static string Serialize(string runId, string recordedBy, string modVersion, long updated, List<JsonObject> fights,
        string? gameVersion = null)
    {
        bool ordered = IndexesOrdered(fights);
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
        if (!string.IsNullOrWhiteSpace(gameVersion)) root["game_version"] = Clip(gameVersion);
        root["updated"] = updated;
        root["fights"] = arr;
        return root.ToJsonString(Json);
    }

    // The whole update: `existing` (the file's current fights, from ParseFights, or empty) gains this fight,
    // and the new file text comes back; null when nothing should be written (the file is full, or even a
    // fight without source lists would push it past the size cap). The new fight shrinks as needed to fit:
    // first with its log and all sources, then without the log (marked "log_dropped") and with fewer sources,
    // and last without its forecast misses. Its misses only fill what the file's other fights leave of
    // MaxMisses. Earlier fights are never rewritten. `existing` is left unchanged.
    //
    // LastWritten is the fight the last AddFight call produced (its run, its index in the file and its floor),
    // or null when that call produced none. A replayed fight keeps the stored fight's index. Game thread only.
    public static (string RunId, int Index, int? Floor)? LastWritten { get; set; }

    public static string? AddFight(List<JsonObject> existing, string runId, string recordedBy, string modVersion,
        long updated, FightMeta? meta, IReadOnlyList<PlayerSources?> perPlayer, IReadOnlyList<string?> steamIds,
        IReadOnlyList<Hit>? hits, bool hitsTruncated, int floorOrdinal, out Outcome outcome,
        IReadOnlyList<ForecastMiss>? misses = null, string? gameVersion = null)
    {
        LastWritten = null;
        outcome = Outcome.Full;
        foreach (var (maxSources, withLog, withMisses) in new[]
                 { (MaxSources, true, true), (MaxSources, false, true), (10, false, true), (0, false, true), (0, false, false) })
        {
            var fights = new List<JsonObject>(existing);
            var fight = BuildFight(meta, perPlayer, steamIds, hits, hitsTruncated, maxSources, withLog, withMisses ? misses : null);
            outcome = Upsert(fights, fight, floorOrdinal, out int at);
            if (outcome == Outcome.Full) return null;
            CapMisses(fights, at);
            string text = Serialize(runId, recordedBy, modVersion, updated, fights, gameVersion);
            if (Encoding.UTF8.GetByteCount(text) <= MaxBytes)
            {
                LastWritten = (runId, WrittenIndex(fights, at), IntOf(fights[at]["floor"]));
                return text;
            }
        }
        outcome = Outcome.TooLarge;
        return null;
    }
}
