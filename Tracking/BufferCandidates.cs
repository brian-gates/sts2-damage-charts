using System;
using System.Collections.Generic;

namespace STS2_DamageCharts.Tracking;

// Hits on a player that cost no HP and may have been cancelled by Buffer, waiting for proof: the player's Buffer
// stacks going down. Pure, so the matching rules are unit-tested; the mod supplies the creatures and stack reads.
//
// No false positives is the binding rule, so:
//   - a player's next hit starting drops their unconfirmed candidate (whatever zeroed the earlier hit, it wasn't
//     a Buffer stack that has not moved since);
//   - one stack lost confirms at most one candidate (the newest ones first), and the rest of that player's
//     candidates are dropped, never credited later;
//   - a candidate not confirmed within the retry window is dropped.
// Game thread only.
internal sealed class BufferCandidates<TKey, TData> where TKey : class
{
    private readonly List<(TKey Key, int Before, long Frame, TData Data)> _list = new();

    public int Count => _list.Count;

    public void Clear() => _list.Clear();

    // A new hit on `key` is starting: an earlier unconfirmed candidate can no longer be proven.
    public void HitStarted(TKey key) => _list.RemoveAll(c => ReferenceEquals(c.Key, key));

    // A hit on `key` that cost no HP, while it had `before` Buffer stacks.
    public void Add(TKey key, int before, long frame, TData data)
    {
        if (before > 0) _list.Add((key, before, frame, data));
    }

    // The confirmed candidates' data, oldest first. `stacks` reads a key's Buffer stacks now; candidates older than
    // `retryFrames` that are still unproven are dropped.
    public List<TData> Check(Func<TKey, int> stacks, long frame, long retryFrames)
    {
        var confirmed = new List<TData>();
        if (_list.Count == 0) return confirmed;
        var keys = new List<TKey>();
        foreach (var c in _list) if (!keys.Exists(k => ReferenceEquals(k, c.Key))) keys.Add(c.Key);
        foreach (var key in keys)
        {
            int now = stacks(key);
            int before = 0;
            foreach (var c in _list) if (ReferenceEquals(c.Key, key)) before = Math.Max(before, c.Before);
            int lost = before - now;
            if (lost <= 0)
            {
                _list.RemoveAll(c => ReferenceEquals(c.Key, key) && frame - c.Frame > retryFrames);
                continue;
            }
            // Newest first, up to one per stack lost; every candidate of this key is settled now.
            var mine = _list.FindAll(c => ReferenceEquals(c.Key, key));
            _list.RemoveAll(c => ReferenceEquals(c.Key, key));
            var take = new List<TData>();
            for (int i = mine.Count - 1; i >= 0 && take.Count < lost; i--)
                if (mine[i].Before > now) take.Add(mine[i].Data);
            take.Reverse();
            confirmed.AddRange(take);
        }
        return confirmed;
    }
}
