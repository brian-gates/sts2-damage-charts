using System;
using System.Collections.Generic;

namespace STS2_DamageCharts;

// A single dealt hit (one damage event) within a round, for the stacked per-attack bar.
internal readonly struct DealtSeg
{
    public readonly int Amount;
    public readonly string? Icon;
    public readonly string Label;
    public DealtSeg(int amount, string? icon, string label) { Amount = amount; Icon = icon; Label = label; }
}

// One source's total contribution (a card name or a power like "Poison").
internal readonly struct SourceEntry
{
    public readonly string Name;
    public readonly long Total;
    public readonly string? Icon; // resource path to a card/relic texture, if any
    public SourceEntry(string name, long total, string? icon) { Name = name; Total = total; Icon = icon; }
}

// One line in the combat log.
internal readonly struct LogEntry
{
    public readonly int Round;
    public readonly string Text;
    public readonly bool Taken;
    public LogEntry(int round, string text, bool taken) { Round = round; Text = text; Taken = taken; }
}

internal sealed class PlayerSources
{
    public List<SourceEntry> Dealt = new();
    public long DealtTotal;
    public List<SourceEntry> Taken = new();
    public long TakenTotal;
}

internal sealed class ChartRow
{
    public readonly int Round;
    public readonly int[] Dealt;
    public readonly int[] Taken;
    public readonly List<DealtSeg>[] DealtSegs; // per slot, ordered individual hits
    public ChartRow(int round, int[] dealt, int[] taken, List<DealtSeg>[] dealtSegs)
    {
        Round = round; Dealt = dealt; Taken = taken; DealtSegs = dealtSegs;
    }
}

internal sealed class ChartSnapshot
{
    public readonly List<ChartRow> Rows;
    public readonly string[] Labels;
    public readonly int PlayerCount;
    public ChartSnapshot(List<ChartRow> rows, string[] labels, int playerCount)
    {
        Rows = rows; Labels = labels; PlayerCount = playerCount;
    }
}

internal sealed class SourceSnapshot
{
    public readonly PlayerSources[] PerPlayer;
    public readonly string[] Labels;
    public readonly int PlayerCount;
    public readonly int LocalSlot;
    public readonly LogEntry[] Log; // most recent lines, oldest-first
    public SourceSnapshot(PlayerSources[] perPlayer, string[] labels, int playerCount, int localSlot, LogEntry[] log)
    {
        PerPlayer = perPlayer; Labels = labels; PlayerCount = playerCount; LocalSlot = localSlot; Log = log;
    }
}

// Thread-safe accumulator. Capture writes on the game/main thread; the UI reads immutable snapshots.
internal sealed class DamageTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<(int Round, int Slot), int[]> _rounds = new();   // [0]=dealt [1]=taken
    private readonly Dictionary<int, Dictionary<string, long>> _dealtBySource = new();
    private readonly Dictionary<int, Dictionary<string, long>> _takenBySource = new();
    private readonly Dictionary<string, string> _sourceIcon = new(); // source label -> texture resource path
    private readonly Dictionary<(int Round, int Slot), List<DealtSeg>> _dealtSegs = new();
    private readonly List<LogEntry> _log = new();
    private const int LogCap = 500;
    private const int LogShow = 300; // detail panel scrolls through this much history
    private int _maxRound;
    private int _playerCount = 1;
    private int _localSlot;
    private string[] _labels = { "You" };

    public void Reset(int playerCount, string[] labels, int localSlot)
    {
        lock (_lock)
        {
            _rounds.Clear();
            _dealtBySource.Clear();
            _takenBySource.Clear();
            _sourceIcon.Clear();
            _dealtSegs.Clear();
            _log.Clear();
            _maxRound = 0;
            _playerCount = Math.Max(1, playerCount);
            _labels = labels;
            _localSlot = Math.Max(0, localSlot);
        }
    }

    public bool HasData()
    {
        lock (_lock) { return _dealtBySource.Count > 0 || _takenBySource.Count > 0; }
    }

    public void AddLog(int round, string text, bool taken)
    {
        lock (_lock)
        {
            _log.Add(new LogEntry(round, text, taken));
            if (_log.Count > LogCap) _log.RemoveRange(0, _log.Count - LogCap);
        }
    }

    public void AddDealt(int round, int slot, int amount, string source, string? icon = null)
        => Add(round, slot, 0, amount, source, _dealtBySource, icon);

    public void AddTaken(int round, int slot, int amount, string source, string? icon = null)
        => Add(round, slot, 1, amount, source, _takenBySource, icon);

    private void Add(int round, int slot, int field, int amount, string source,
                     Dictionary<int, Dictionary<string, long>> bySource, string? icon)
    {
        if (amount <= 0 || slot < 0) return;
        lock (_lock)
        {
            var key = (round, slot);
            if (!_rounds.TryGetValue(key, out var vals)) { vals = new int[2]; _rounds[key] = vals; }
            vals[field] += amount;
            if (round > _maxRound) _maxRound = round;

            if (!bySource.TryGetValue(slot, out var map)) { map = new Dictionary<string, long>(); bySource[slot] = map; }
            map.TryGetValue(source, out long cur);
            map[source] = cur + amount;

            if (icon != null && !_sourceIcon.ContainsKey(source)) _sourceIcon[source] = icon;

            if (field == 0) // dealt: remember the individual hit for the stacked per-attack bar
            {
                if (!_dealtSegs.TryGetValue(key, out var segs)) { segs = new List<DealtSeg>(); _dealtSegs[key] = segs; }
                segs.Add(new DealtSeg(amount, icon, source));
            }
        }
    }

    public ChartSnapshot Snapshot()
    {
        lock (_lock)
        {
            var rows = new List<ChartRow>();
            if (_rounds.Count == 0) return new ChartSnapshot(rows, (string[])_labels.Clone(), _playerCount);
            // Derive the actual round range from recorded data (don't assume rounds start at 1).
            int minR = int.MaxValue, maxR = int.MinValue;
            foreach (var k in _rounds.Keys) { if (k.Round < minR) minR = k.Round; if (k.Round > maxR) maxR = k.Round; }
            for (int r = minR; r <= maxR; r++)
            {
                var dealt = new int[_playerCount];
                var taken = new int[_playerCount];
                var segs = new List<DealtSeg>[_playerCount];
                for (int s = 0; s < _playerCount; s++)
                {
                    if (_rounds.TryGetValue((r, s), out var v)) { dealt[s] = v[0]; taken[s] = v[1]; }
                    segs[s] = _dealtSegs.TryGetValue((r, s), out var ls) ? ls : new List<DealtSeg>(0);
                }
                rows.Add(new ChartRow(r, dealt, taken, segs));
            }
            return new ChartSnapshot(rows, (string[])_labels.Clone(), _playerCount);
        }
    }

    public SourceSnapshot SourceSnapshot()
    {
        lock (_lock)
        {
            var perPlayer = new PlayerSources[_playerCount];
            for (int s = 0; s < _playerCount; s++)
            {
                var ps = new PlayerSources();
                ps.Dealt = SortedList(_dealtBySource, s, out ps.DealtTotal);
                ps.Taken = SortedList(_takenBySource, s, out ps.TakenTotal);
                perPlayer[s] = ps;
            }
            int from = Math.Max(0, _log.Count - LogShow);
            var log = _log.GetRange(from, _log.Count - from).ToArray();
            return new SourceSnapshot(perPlayer, (string[])_labels.Clone(), _playerCount, _localSlot, log);
        }
    }

    private List<SourceEntry> SortedList(Dictionary<int, Dictionary<string, long>> bySource, int slot, out long total)
    {
        total = 0;
        var list = new List<SourceEntry>();
        if (bySource.TryGetValue(slot, out var map))
        {
            foreach (var kv in map)
            {
                _sourceIcon.TryGetValue(kv.Key, out var icon);
                list.Add(new SourceEntry(kv.Key, kv.Value, icon));
                total += kv.Value;
            }
            list.Sort((a, b) => b.Total.CompareTo(a.Total));
        }
        return list;
    }
}
