using System;
using System.Collections.Generic;
using STS2_DamageCharts.Forecast;

namespace STS2_DamageCharts.Tracking;

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
    private long[] _absorbed = Array.Empty<long>();
    private long[] _buffered = Array.Empty<long>();
    private Dictionary<string, long>[] _dealtBySource = Array.Empty<Dictionary<string, long>>();
    private Dictionary<string, long>[] _takenBySource = Array.Empty<Dictionary<string, long>>();
    private readonly Dictionary<string, string> _sourceIcon = new();

    private readonly List<PlayerSources?[]> _encSources = new(); // per encounter: that fight's by-source breakdown per slot
    private readonly List<FightMeta?> _encMeta = new();          // per encounter: floor / name / elite-boss / rounds
    private readonly List<LogEntry> _log = new();
    private int _encounters;
    private string? _runKey; // identity of the run these fights belong to (its run id); null = not known yet
    // RunSourceSnapshot, built once per change (Fold / Clear): the recap renders it every frame, and its log
    // (up to LogCap fat entries) would otherwise be a fresh large-object-heap array each frame.
    private SourceSnapshot? _snapshot;

    public bool HasData() => _encounters > 0;
    public int FightCount => _encounters;
    public string? RunKey => _runKey;

    // Bind the aggregate to one run. A different known key (another run was loaded, e.g. a solo run after a
    // co-op one) drops everything recorded so far, so the recap never mixes runs. Null (identity unreadable)
    // keeps the current data. Returns true when it cleared.
    public bool EnsureRun(string? key)
    {
        if (key == null || key == _runKey) return false;
        bool had = _runKey != null || _encounters > 0;
        Clear();
        _runKey = key;
        return had;
    }

    public void Clear()
    {
        _playerCount = 0;
        _localSlot = 0;
        _labels = Array.Empty<string>();
        _dealt = _taken = _heal = _block = _absorbed = _buffered = Array.Empty<long>();
        _dealtBySource = _takenBySource = Array.Empty<Dictionary<string, long>>();
        _sourceIcon.Clear();
        _snapshot = null;
        _encSources.Clear();
        _encMeta.Clear();
        _log.Clear();
        _encounters = 0;
        _runKey = null;
    }

    // Add one finished combat. Reads the per-combat snapshots taken before the tracker is reset. `meta` is
    // whatever was captured about the fight (may be null or partial); a missing round count falls back to
    // the last round that recorded damage.
    public void Fold(SourceSnapshot src, ChartSnapshot chart, FightMeta? meta = null)
    {
        if (src.PlayerCount <= 0) return;
        if (_playerCount != src.PlayerCount)
        {
            // A different player count is a different run (or a misread): never fold it into the old fights,
            // whose per-slot arrays have the old size (that also showed phantom extra players).
            string? key = _runKey;
            Clear();
            _runKey = key;
            Init(src);
        }

        _encounters++;
        _snapshot = null;
        for (int s = 0; s < _playerCount && s < src.PerPlayer.Length; s++)
        {
            var ps = src.PerPlayer[s];
            if (ps == null) continue;
            _dealt[s] += ps.DealtTotal;
            _taken[s] += ps.TakenTotal;
            _heal[s] += ps.HealTotal;
            _block[s] += ps.BlockTotal;
            _absorbed[s] += ps.AbsorbedTotal;
            _buffered[s] += ps.BufferedTotal;
            Merge(_dealtBySource[s], ps.Dealt);
            Merge(_takenBySource[s], ps.Taken);
        }
        var encSrc = new PlayerSources?[_playerCount];
        for (int s = 0; s < _playerCount && s < src.PerPlayer.Length; s++) encSrc[s] = src.PerPlayer[s];
        _encSources.Add(encSrc);
        var m = meta?.Clone() ?? new FightMeta();
        if (m.Rounds <= 0)
            foreach (var r in chart.Rows) if (r.Round > m.Rounds) m.Rounds = r.Round;
        _encMeta.Add(m);

        _log.Add(LogEntry.FightSeparator(_encounters));
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
        _absorbed = new long[_playerCount];
        _buffered = new long[_playerCount];
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

    // One finished fight's captured data (1-based), for the run stats file. False when out of range.
    public bool TryGetFight(int fight, out FightMeta meta, out PlayerSources?[] sources)
    {
        int i = fight - 1;
        if (i < 0 || i >= _encSources.Count) { meta = new FightMeta(); sources = Array.Empty<PlayerSources?>(); return false; }
        meta = _encMeta[i] ?? new FightMeta();
        sources = _encSources[i];
        return true;
    }

    // The run so far as one snapshot. Cached until the next Fold or Clear; callers only read it.
    public SourceSnapshot RunSourceSnapshot()
    {
        if (_snapshot != null) return _snapshot;
        var perPlayer = new PlayerSources[_playerCount];
        for (int s = 0; s < _playerCount; s++)
        {
            var ps = new PlayerSources
            {
                Dealt = SortedList(_dealtBySource[s], out long dt),
                Taken = SortedList(_takenBySource[s], out long tt),
                HealTotal = _heal[s],
                BlockTotal = _block[s],
                AbsorbedTotal = _absorbed[s],
                BufferedTotal = _buffered[s],
            };
            ps.DealtTotal = dt;
            ps.TakenTotal = tt;
            perPlayer[s] = ps;
        }
        _snapshot = new SourceSnapshot(perPlayer, (string[])_labels.Clone(), _playerCount, _localSlot, _log.ToArray());
        return _snapshot;
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
