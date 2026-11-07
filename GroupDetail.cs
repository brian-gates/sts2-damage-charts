using System;
using System.Collections.Generic;

namespace STS2_DamageCharts;

// Pure (no Godot / game refs) model + helpers behind the chart hover tooltips: what one bar group (a
// round of the current combat, or a whole fight in the run recap) holds per player, and the hit-test that
// maps the cursor to a group. Compiled straight into the unit tests.

internal enum FightKind { Unknown, Normal, Elite, Boss }

// What we know about one combat beyond its damage: where it was and what it was. Every field is optional
// (0 / null / Unknown) because each comes from a separate best-effort read of the game's run state.
internal sealed class FightMeta
{
    public int Floor;          // run-wide floor number (IRunState.TotalFloor); 0 = unknown
    public string? Name;       // encounter title, else the enemies' names; null = unknown
    public FightKind Kind;     // from the encounter's / room's RoomType
    public int Rounds;         // highest round number reached; 0 = unknown

    public FightMeta Clone() => new() { Floor = Floor, Name = Name, Kind = Kind, Rounds = Rounds };

    // RoomType / MapPointType enum names ("Monster", "Elite", "Boss"); anything else is not a fight tag.
    public static FightKind KindFromRoomType(string? roomType)
    {
        if (string.IsNullOrWhiteSpace(roomType)) return FightKind.Unknown;
        string t = roomType.Trim();
        if (t.Equals("Boss", StringComparison.OrdinalIgnoreCase)) return FightKind.Boss;
        if (t.Equals("Elite", StringComparison.OrdinalIgnoreCase)) return FightKind.Elite;
        if (t.Equals("Monster", StringComparison.OrdinalIgnoreCase)) return FightKind.Normal;
        return FightKind.Unknown;
    }
}

// One source line in a tooltip section: total, how many hits made it (xN, per-round only) and its share.
internal readonly struct DetailRow
{
    public readonly string Name;
    public readonly long Amount;
    public readonly int Count;     // hits; 0 when not tracked
    public readonly string? Icon;
    public readonly int Pct;       // share of the section total, rounded
    public DetailRow(string name, long amount, int count, string? icon, int pct)
    { Name = name; Amount = amount; Count = count; Icon = icon; Pct = pct; }
}

internal sealed class PlayerDetail
{
    public int Slot;
    public string Label = "";                 // shown only when the group has more than one player
    public long DealtTotal;
    public List<DetailRow> Dealt = new();     // sorted by amount, descending (all of them; the view trims)
    public long TakenTotal;
    public List<DetailRow> Taken = new();     // empty when the taken side has no by-source breakdown
    public long? Heal;                        // null = not recorded for this scope
    public long? Block;
}

internal sealed class GroupDetail
{
    public string Title = "";
    public FightKind Kind;                    // Elite / Boss get a tag beside the title
    public string? Subtitle;                  // encounter name
    public string? Note;                      // e.g. "4 rounds"
    public bool ShowPct;                      // amounts read "12 (40%)"
    public int MaxRows = 5;                   // per source list, before "+ N more"
    public List<PlayerDetail> Players = new();
}

internal static class GroupDetails
{
    public const string Sep = " · ";

    public static int Pct(long part, long total) => total > 0 ? (int)Math.Round(100.0 * part / total) : 0;

    // Sources sorted by amount (stable for ties), each with its share of `total`.
    public static List<DetailRow> Rows(IReadOnlyList<SourceEntry> entries, long total)
    {
        var list = new List<DetailRow>(entries.Count);
        foreach (var e in entries) list.Add(new DetailRow(e.Name, e.Total, 0, e.Icon, Pct(e.Total, total)));
        StableSortDesc(list);
        return list;
    }

    // One round's individual dealt hits folded per source: total, hit count and the first icon seen,
    // sorted by total (first-seen order breaks ties).
    public static List<(string Label, long Sum, int Count, string? Icon)> AggregateBySource(List<DealtSeg> segs)
    {
        var order = new List<string>();
        var sum = new Dictionary<string, long>();
        var count = new Dictionary<string, int>();
        var icon = new Dictionary<string, string?>();
        foreach (var seg in segs)
        {
            if (!sum.ContainsKey(seg.Label)) { order.Add(seg.Label); sum[seg.Label] = 0; count[seg.Label] = 0; icon[seg.Label] = seg.Icon; }
            sum[seg.Label] += seg.Amount;
            count[seg.Label] += 1;
            if (icon[seg.Label] == null) icon[seg.Label] = seg.Icon;
        }
        var list = new List<(string Label, long Sum, int Count, string? Icon)>(order.Count);
        foreach (var label in order) list.Add((label, sum[label], count[label], icon[label]));
        // List.Sort is unstable; sort by (sum desc, first-seen asc) so equal sources keep their order.
        var idx = new Dictionary<string, int>(order.Count);
        for (int i = 0; i < order.Count; i++) idx[order[i]] = i;
        list.Sort((a, b) => a.Sum != b.Sum ? b.Sum.CompareTo(a.Sum) : idx[a.Label].CompareTo(idx[b.Label]));
        return list;
    }

    // "Fight 3 · Floor 7", or just "Fight 3" when the floor is unknown.
    public static string FightTitle(int fight, FightMeta? meta) =>
        meta != null && meta.Floor > 0 ? $"Fight {fight}{Sep}Floor {meta.Floor}" : $"Fight {fight}";

    public static string? RoundsNote(int rounds) => rounds <= 0 ? null : rounds == 1 ? "1 round" : $"{rounds} rounds";

    // A finished fight from the run aggregate: per player, dealt + taken by source, heal and block.
    public static GroupDetail ForFight(int fight, FightMeta? meta, IReadOnlyList<PlayerSources?> perPlayer, string[] labels)
    {
        var d = new GroupDetail
        {
            Title = FightTitle(fight, meta),
            Kind = meta?.Kind ?? FightKind.Unknown,
            Subtitle = string.IsNullOrWhiteSpace(meta?.Name) ? null : meta!.Name,
            Note = RoundsNote(meta?.Rounds ?? 0),
            ShowPct = true,
            MaxRows = 5,
        };
        for (int s = 0; s < perPlayer.Count; s++)
        {
            var ps = perPlayer[s];
            if (ps == null) continue;
            d.Players.Add(new PlayerDetail
            {
                Slot = s,
                Label = PlayerLabel(labels, s),
                DealtTotal = ps.DealtTotal,
                Dealt = Rows(ps.Dealt, ps.DealtTotal),
                TakenTotal = ps.TakenTotal,
                Taken = Rows(ps.Taken, ps.TakenTotal),
                Heal = ps.HealTotal,
                Block = ps.BlockTotal,
            });
        }
        return d;
    }

    // One round of the current combat: dealt by source (from the round's hits) and the taken total (the
    // tracker keeps no per-round taken breakdown). `onlySlot` limits it to one player (the compact chart
    // hovers a single player's cell); otherwise every player gets a section.
    public static GroupDetail ForRound(ChartRow row, string[] labels, int playerCount, int? onlySlot, bool showPct, int maxRows)
    {
        string title = $"Round {row.Round}";
        if (onlySlot is int os && playerCount > 1) title += Sep + PlayerLabel(labels, os);
        var d = new GroupDetail { Title = title, ShowPct = showPct, MaxRows = maxRows };
        for (int s = 0; s < playerCount; s++)
        {
            if (onlySlot is int only && s != only) continue;
            var segs = s < row.DealtSegs.Length ? row.DealtSegs[s] : null;
            var agg = segs != null ? AggregateBySource(segs) : new List<(string Label, long Sum, int Count, string? Icon)>();
            long dealt = 0;
            foreach (var a in agg) dealt += a.Sum;
            var rows = new List<DetailRow>(agg.Count);
            foreach (var a in agg) rows.Add(new DetailRow(a.Label, a.Sum, a.Count, a.Icon, Pct(a.Sum, dealt)));
            d.Players.Add(new PlayerDetail
            {
                Slot = s,
                // A single-player section never repeats the name (it is in the title or there is one player).
                Label = onlySlot == null && playerCount > 1 ? PlayerLabel(labels, s) : "",
                DealtTotal = dealt,
                Dealt = rows,
                TakenTotal = s < row.Taken.Length ? row.Taken[s] : 0,
            });
        }
        return d;
    }

    public static string PlayerLabel(string[] labels, int slot) =>
        slot >= 0 && slot < labels.Length && !string.IsNullOrEmpty(labels[slot]) ? labels[slot] : $"P{slot + 1}";

    // Index of the bar group under (mx, my), or -1. Groups sit side by side from `left`, each `groupW`
    // wide; the band runs from `top` to `bottom` (callers include the axis labels under the bars).
    public static int GroupAt(float mx, float my, float left, float top, float bottom, float groupW, int groups)
    {
        if (groups <= 0 || groupW <= 0f || float.IsNaN(mx) || float.IsNaN(my)) return -1;
        if (my < top || my > bottom || mx < left) return -1;
        int i = (int)Math.Floor((mx - left) / groupW);
        return i >= 0 && i < groups ? i : -1;
    }

    // Encounter titles come from localization; a missing entry can surface as its raw key
    // ("ENCOUNTER.FOO.title") instead of a name, which is worse than falling back to the enemies.
    public static bool LooksLikeLocKey(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return true;
        string t = s.Trim();
        if (t.IndexOf(' ') >= 0) return false;
        if (t.EndsWith(".title", StringComparison.OrdinalIgnoreCase) || t.EndsWith(".name", StringComparison.OrdinalIgnoreCase)) return true;
        bool hasLower = false, hasSep = false;
        foreach (char c in t) { if (char.IsLower(c)) hasLower = true; if (c is '_' or '.') hasSep = true; }
        return hasSep && !hasLower;
    }

    // "Jaw Worm", "Cultist, Louse", "Cultist, Louse +2": distinct names in first-seen order.
    public static string? JoinNames(IEnumerable<string?> names, int max = 2)
    {
        var distinct = new List<string>();
        foreach (var n in names)
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            string t = n.Trim();
            if (!distinct.Contains(t)) distinct.Add(t);
        }
        if (distinct.Count == 0) return null;
        int shown = Math.Min(distinct.Count, Math.Max(1, max));
        string s = string.Join(", ", distinct.GetRange(0, shown));
        return distinct.Count > shown ? $"{s} +{distinct.Count - shown}" : s;
    }

    private static void StableSortDesc(List<DetailRow> list)
    {
        var keyed = new List<(DetailRow Row, int I)>(list.Count);
        for (int i = 0; i < list.Count; i++) keyed.Add((list[i], i));
        keyed.Sort((a, b) => a.Row.Amount != b.Row.Amount ? b.Row.Amount.CompareTo(a.Row.Amount) : a.I.CompareTo(b.I));
        for (int i = 0; i < list.Count; i++) list[i] = keyed[i].Row;
    }
}
