using System;
using System.Collections.Generic;
using STS2_DamageCharts.Forecast;

namespace STS2_DamageCharts.Tracking;

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
    public readonly int Hits;     // HP changes that made up Total; 0 = not counted for this scope
    public readonly string? Id;   // the game's model id ("CARD.STRIKE_SILENT"); null = unknown or ambiguous
    public SourceEntry(string name, long total, string? icon, int hits = 0, string? id = null)
    { Name = name; Total = total; Icon = icon; Hits = hits; Id = id; }
}

// One line in the combat log. Kind is null for a non-hit line. Fight > 0 marks the run recap's separator before
// fight N (Round is unused there; its Text is "Fight N"). Amount, Slot
// (whose line it is), Icon (the source's art path) and Name (the source's name, for an icon by name) come from
// the hit, for the line's icon and amount bar.
internal readonly struct LogEntry
{
    public readonly int Round;
    public readonly string Text;
    public readonly LogTone Tone;
    public readonly HitKind? Kind;
    public readonly long Amount;
    public readonly int Slot;
    public readonly string? Icon;
    public readonly string? Name;
    public readonly int Fight;
    // The line as drawn (LogText): Lead, then AmountText ("8", "11 saved", "+6"; null = Lead only). ShortLead is
    // Lead without the target, used for dealt lines when the log has a single enemy. Target: the hit's target name.
    public readonly string Lead;
    public readonly string ShortLead;
    public readonly string? AmountText;
    public readonly string? Target;
    public LogEntry(int round, string text, LogTone tone, HitKind? kind = null, long amount = 0, int slot = -1,
        string? icon = null, string? name = null, int fight = 0, string? lead = null, string? amountText = null,
        string? shortLead = null, string? target = null)
    {
        Round = round; Text = text; Tone = tone; Kind = kind; Amount = amount; Slot = slot; Icon = icon; Name = name;
        Fight = fight; Lead = lead ?? text; ShortLead = shortLead ?? Lead; AmountText = amountText; Target = target;
    }

    public bool IsFightSeparator => Fight > 0;

    public static LogEntry FightSeparator(int fight) => new(0, $"Fight {fight}", LogTone.Plain, fight: Math.Max(1, fight));

    public static LogEntry Of(Hit h, Func<int, string> playerName, bool coop)
    {
        var (lead, amount) = LogText.Format(h, playerName, coop, showTarget: true);
        string shortLead = h.Kind == HitKind.Dealt ? LogText.Format(h, playerName, coop, showTarget: false).Lead : lead;
        return new(h.Round, h.Text(playerName), h.Tone, h.Kind, h.Amount, h.Slot, h.Icon, h.Source,
            lead: lead, amountText: amount, shortLead: shortLead, target: h.Target);
    }
}

internal sealed class PlayerSources
{
    public List<SourceEntry> Dealt = new();
    public long DealtTotal;
    public List<SourceEntry> Taken = new();
    public long TakenTotal;
    public long HealTotal;   // total HP healed this scope (no by-source breakdown)
    public long BlockTotal;  // total block gained this scope (no by-source breakdown)
    public long AbsorbedTotal; // attack damage the player's pet (Wuppy, Osty) lost in their place
    public long BufferedTotal; // HP the player's Buffer saved (the cancelled hits' intended HP loss); not HP lost
    public List<SourceEntry> Potions = new();  // potions used this scope: name, times used, icon
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
    public readonly IReadOnlyList<Hit> FightHits;  // every hit of the fight, first DamageTracker.HitCap kept
    public readonly bool HitsTruncated;
    public SourceSnapshot(PlayerSources[] perPlayer, string[] labels, int playerCount, int localSlot, LogEntry[] log,
        IReadOnlyList<Hit>? fightHits = null, bool hitsTruncated = false)
    {
        PerPlayer = perPlayer; Labels = labels; PlayerCount = playerCount; LocalSlot = localSlot; Log = log;
        FightHits = fightHits ?? Array.Empty<Hit>(); HitsTruncated = hitsTruncated;
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
    // Field: 0 dealt, 1 taken, 2 potions. Hits = HP changes per source. Ids = the game model behind a source
    // label; a label seen with two different ids in one list (two monsters sharing a name) is ambiguous
    // and stored as "" so it reports no id rather than a guess.
    private readonly Dictionary<(int Field, int Slot, string Source), int> _hits = new();
    private readonly Dictionary<(int Field, int Slot, string Source), string> _ids = new();
    private readonly Dictionary<(int Round, int Slot), List<DealtSeg>> _dealtSegs = new();
    private readonly Dictionary<int, long> _healByPlayer = new();   // slot -> total healed
    private readonly Dictionary<int, long> _blockByPlayer = new();  // slot -> total block gained
    private readonly Dictionary<int, long> _absorbedByPlayer = new(); // slot -> damage their pet took for them
    private readonly Dictionary<int, long> _bufferedByPlayer = new(); // slot -> HP their Buffer saved
    private readonly Dictionary<int, Dictionary<string, long>> _potionsByPlayer = new(); // slot -> potion -> uses
    private readonly List<LogEntry> _log = new();
    private const int LogCap = 500;
    public const int HitCap = 600;   // hits kept per fight for the shared log
    private readonly List<Hit> _fightHits = new();
    private bool _hitsTruncated;
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
            _hits.Clear();
            _ids.Clear();
            _dealtSegs.Clear();
            _healByPlayer.Clear();
            _blockByPlayer.Clear();
            _absorbedByPlayer.Clear();
            _bufferedByPlayer.Clear();
            _potionsByPlayer.Clear();
            _log.Clear();
            _fightHits.Clear();
            _hitsTruncated = false;
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

    // Records a hit: the in-game log line is drawn from it, and the first HitCap are kept as data.
    public void AddHit(Hit h)
    {
        lock (_lock)
        {
            _log.Add(LogEntry.Of(h, PlayerLabel, _playerCount > 1));
            if (_log.Count > LogCap) _log.RemoveRange(0, _log.Count - LogCap);
            if (_fightHits.Count < HitCap) _fightHits.Add(h); else _hitsTruncated = true;
        }
    }

    private string PlayerLabel(int slot) => slot >= 0 && slot < _labels.Length ? _labels[slot] : "?";

    public void AddDealt(int round, int slot, int amount, string source, string? icon = null, string? id = null)
        => Add(round, slot, 0, amount, source, _dealtBySource, icon, id);

    public void AddTaken(int round, int slot, int amount, string source, string? icon = null, string? id = null)
        => Add(round, slot, 1, amount, source, _takenBySource, icon, id);

    public void AddHealed(int slot, int amount) => AddTotal(_healByPlayer, slot, amount);

    public void AddBlock(int slot, int amount) => AddTotal(_blockByPlayer, slot, amount);

    public void AddAbsorbed(int slot, int amount) => AddTotal(_absorbedByPlayer, slot, amount);

    public void AddBuffered(int slot, int amount) => AddTotal(_bufferedByPlayer, slot, amount);

    // HP this player has lost so far this fight (the sum of their taken sources). For the forecast check.
    public long TakenTotal(int slot)
    {
        lock (_lock)
        {
            long total = 0;
            if (_takenBySource.TryGetValue(slot, out var map)) foreach (var v in map.Values) total += v;
            return total;
        }
    }

    // A potion drunk/thrown this combat. Counted per player (not damage, so it stays out of the bars)
    // and shares the source-icon map so the summary can show its art.
    public void AddPotion(int slot, string name, string? icon, string? id = null)
    {
        if (slot < 0 || string.IsNullOrEmpty(name)) return;
        lock (_lock)
        {
            NoteIdLocked(2, slot, name, id);
            if (!_potionsByPlayer.TryGetValue(slot, out var map)) { map = new Dictionary<string, long>(); _potionsByPlayer[slot] = map; }
            map.TryGetValue(name, out long cur);
            map[name] = cur + 1;
            if (icon != null && !_sourceIcon.ContainsKey(name)) _sourceIcon[name] = icon;
        }
    }

    private void NoteIdLocked(int field, int slot, string source, string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        var k = (field, slot, source);
        if (!_ids.TryGetValue(k, out var known)) _ids[k] = id!;
        else if (known.Length > 0 && known != id) _ids[k] = ""; // ambiguous
    }

    private void AddTotal(Dictionary<int, long> byPlayer, int slot, int amount)
    {
        if (amount <= 0 || slot < 0) return;
        lock (_lock)
        {
            byPlayer.TryGetValue(slot, out long cur);
            byPlayer[slot] = cur + amount;
        }
    }

    private void Add(int round, int slot, int field, int amount, string source,
                     Dictionary<int, Dictionary<string, long>> bySource, string? icon, string? id)
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

            var hk = (field, slot, source);
            _hits.TryGetValue(hk, out int h);
            _hits[hk] = h + 1;
            NoteIdLocked(field, slot, source, id);

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
                ps.Dealt = SortedList(_dealtBySource, s, out ps.DealtTotal, field: 0);
                ps.Taken = SortedList(_takenBySource, s, out ps.TakenTotal, field: 1);
                _healByPlayer.TryGetValue(s, out ps.HealTotal);
                _blockByPlayer.TryGetValue(s, out ps.BlockTotal);
                _absorbedByPlayer.TryGetValue(s, out ps.AbsorbedTotal);
                _bufferedByPlayer.TryGetValue(s, out ps.BufferedTotal);
                ps.Potions = SortedList(_potionsByPlayer, s, out _, field: 2);
                perPlayer[s] = ps;
            }
            int from = Math.Max(0, _log.Count - LogShow);
            var log = _log.GetRange(from, _log.Count - from).ToArray();
            return new SourceSnapshot(perPlayer, (string[])_labels.Clone(), _playerCount, _localSlot, log, _fightHits.ToArray(), _hitsTruncated);
        }
    }

    // field: 0 dealt / 1 taken / 2 potions, for the hit counts and ids (potions count uses in Total, not hits).
    private List<SourceEntry> SortedList(Dictionary<int, Dictionary<string, long>> bySource, int slot, out long total, int field)
    {
        total = 0;
        var list = new List<SourceEntry>();
        if (bySource.TryGetValue(slot, out var map))
        {
            foreach (var kv in map)
            {
                _sourceIcon.TryGetValue(kv.Key, out var icon);
                string? id = _ids.TryGetValue((field, slot, kv.Key), out var known) && known.Length > 0 ? known : null;
                _hits.TryGetValue((field, slot, kv.Key), out int hits);
                list.Add(new SourceEntry(kv.Key, kv.Value, icon, hits, id));
                total += kv.Value;
            }
            list.Sort((a, b) => b.Total.CompareTo(a.Total));
        }
        return list;
    }
}
