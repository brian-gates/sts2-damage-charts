using System;
using System.Collections.Generic;

namespace STS2_DamageCharts;

// Run-scoped accumulator: folds each finished combat's per-combat snapshot into run-wide totals,
// by-source breakdowns, a per-encounter chart, and a combat log. Emits the SAME ChartSnapshot /
// SourceSnapshot types the per-combat views consume, so the existing renderer is reused unchanged.
//
// Touched only on the game thread (Fold at combat end; the snapshot getters from the per-frame Tick),
// so unlike DamageTracker it needs no lock.
internal sealed class RunAggregate
{
    private const int LogCap = 2000; // run-wide; larger than the per-combat cap since it spans many fights

    private int _playerCount;
    private int _localSlot;
    private string[] _labels = Array.Empty<string>();

    private long[] _dealt = Array.Empty<long>();
    private long[] _taken = Array.Empty<long>();
    private long[] _heal = Array.Empty<long>();
    private long[] _block = Array.Empty<long>();
    private Dictionary<string, long>[] _dealtBySource = Array.Empty<Dictionary<string, long>>();
    private Dictionary<string, long>[] _takenBySource = Array.Empty<Dictionary<string, long>>();
    private readonly Dictionary<string, string> _sourceIcon = new();

    private readonly List<int[]> _encDealt = new(); // per encounter: dealt[slot]
    private readonly List<int[]> _encTaken = new(); // per encounter: taken[slot]
    private readonly List<long[]> _encHeal = new(); // per encounter: heal[slot]
    private readonly List<long[]> _encBlock = new();// per encounter: block[slot]
    private readonly List<LogEntry> _log = new();
    private int _encounters;

    public bool HasData() => _encounters > 0;

    // One finished combat's totals for a given player slot, for the in-combat run-history band.
    public readonly struct EncounterStat
    {
        public readonly int Fight;
        public readonly long Dealt, Taken, Heal, Block;
        public EncounterStat(int fight, long dealt, long taken, long heal, long block)
        { Fight = fight; Dealt = dealt; Taken = taken; Heal = heal; Block = block; }
    }

    public List<EncounterStat> EncounterStats(int slot)
    {
        var list = new List<EncounterStat>();
        if (slot < 0 || slot >= _playerCount) return list;
        for (int i = 0; i < _encDealt.Count; i++)
            list.Add(new EncounterStat(i + 1, _encDealt[i][slot], _encTaken[i][slot], _encHeal[i][slot], _encBlock[i][slot]));
        return list;
    }

    public void Clear()
    {
        _playerCount = 0;
        _localSlot = 0;
        _labels = Array.Empty<string>();
        _dealt = _taken = _heal = _block = Array.Empty<long>();
        _dealtBySource = _takenBySource = Array.Empty<Dictionary<string, long>>();
        _sourceIcon.Clear();
        _encDealt.Clear();
        _encTaken.Clear();
        _encHeal.Clear();
        _encBlock.Clear();
        _log.Clear();
        _encounters = 0;
    }

    // Add one finished combat. Reads the per-combat snapshots taken before the tracker is reset.
    public void Fold(SourceSnapshot src, ChartSnapshot chart)
    {
        if (src.PlayerCount <= 0) return;
        if (_playerCount != src.PlayerCount) Init(src);

        _encounters++;
        var encD = new int[_playerCount];
        var encT = new int[_playerCount];
        var encH = new long[_playerCount];
        var encB = new long[_playerCount];
        for (int s = 0; s < _playerCount && s < src.PerPlayer.Length; s++)
        {
            var ps = src.PerPlayer[s];
            if (ps == null) continue;
            _dealt[s] += ps.DealtTotal;
            _taken[s] += ps.TakenTotal;
            _heal[s] += ps.HealTotal;
            _block[s] += ps.BlockTotal;
            Merge(_dealtBySource[s], ps.Dealt);
            Merge(_takenBySource[s], ps.Taken);
            encD[s] = (int)Math.Min(int.MaxValue, ps.DealtTotal);
            encT[s] = (int)Math.Min(int.MaxValue, ps.TakenTotal);
            encH[s] = ps.HealTotal;
            encB[s] = ps.BlockTotal;
        }
        _encDealt.Add(encD);
        _encTaken.Add(encT);
        _encHeal.Add(encH);
        _encBlock.Add(encB);

        _log.Add(new LogEntry(_encounters, $"──── Fight {_encounters} ────", false));
        foreach (var e in src.Log) _log.Add(e);
        if (_log.Count > LogCap) _log.RemoveRange(0, _log.Count - LogCap);
    }

    private void Init(SourceSnapshot src)
    {
        _playerCount = src.PlayerCount;
        _localSlot = Math.Clamp(src.LocalSlot, 0, _playerCount - 1);
        _labels = (string[])src.Labels.Clone();
        _dealt = new long[_playerCount];
        _taken = new long[_playerCount];
        _heal = new long[_playerCount];
        _block = new long[_playerCount];
        _dealtBySource = new Dictionary<string, long>[_playerCount];
        _takenBySource = new Dictionary<string, long>[_playerCount];
        for (int s = 0; s < _playerCount; s++)
        {
            _dealtBySource[s] = new Dictionary<string, long>();
            _takenBySource[s] = new Dictionary<string, long>();
        }
    }

    private void Merge(Dictionary<string, long> into, List<SourceEntry> entries)
    {
        foreach (var e in entries)
        {
            into.TryGetValue(e.Name, out long cur);
            into[e.Name] = cur + e.Total;
            if (e.Icon != null && !_sourceIcon.ContainsKey(e.Name)) _sourceIcon[e.Name] = e.Icon;
        }
    }

    public SourceSnapshot RunSourceSnapshot()
    {
        var perPlayer = new PlayerSources[_playerCount];
        for (int s = 0; s < _playerCount; s++)
        {
            var ps = new PlayerSources
            {
                Dealt = SortedList(_dealtBySource[s], out long dt),
                Taken = SortedList(_takenBySource[s], out long tt),
                HealTotal = _heal[s],
                BlockTotal = _block[s],
            };
            ps.DealtTotal = dt;
            ps.TakenTotal = tt;
            perPlayer[s] = ps;
        }
        return new SourceSnapshot(perPlayer, (string[])_labels.Clone(), _playerCount, _localSlot, _log.ToArray());
    }

    // Per-encounter chart: one row per combat, with the combat's dealt/taken totals. The "Round" field
    // carries the 1-based encounter index; one dealt segment per slot so the bar renders at full height.
    public ChartSnapshot RunChartSnapshot()
    {
        var rows = new List<ChartRow>(_encDealt.Count);
        for (int i = 0; i < _encDealt.Count; i++)
        {
            var dealt = _encDealt[i];
            var taken = _encTaken[i];
            var segs = new List<DealtSeg>[_playerCount];
            for (int s = 0; s < _playerCount; s++)
                segs[s] = new List<DealtSeg> { new DealtSeg(dealt[s], null, "") };
            rows.Add(new ChartRow(i + 1, dealt, taken, segs));
        }
        return new ChartSnapshot(rows, (string[])_labels.Clone(), _playerCount);
    }

    private List<SourceEntry> SortedList(Dictionary<string, long> map, out long total)
    {
        total = 0;
        var list = new List<SourceEntry>();
        foreach (var kv in map)
        {
            _sourceIcon.TryGetValue(kv.Key, out var icon);
            list.Add(new SourceEntry(kv.Key, kv.Value, icon));
            total += kv.Value;
        }
        list.Sort((a, b) => b.Total.CompareTo(a.Total));
        return list;
    }
}
