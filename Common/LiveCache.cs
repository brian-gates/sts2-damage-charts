using System;
using System.Collections.Generic;

namespace STS2_DamageCharts.Common;

// A keyed cache of engine objects that never hands out one the engine has since freed.
//
// The game's AssetCache disposes the textures it loaded outside preloading ("Asset not cached") at every room
// transition (PreloadManager -> UnloadMissedCacheAssets). ResourceLoader.Load(..., CacheMode.Reuse) gives us the
// very same managed wrapper the game holds, so a texture we cached in one fight (an orb icon, a relic picture)
// is a disposed object in the next, and assigning it throws ObjectDisposedException. Every hit is checked with
// `isLive` (GodotObject.IsInstanceValid in the mod) and a dead entry is loaded again. A cached miss (null) stays
// a miss: the caller's loader decides what is worth probing.
internal sealed class LiveCache<T> where T : class
{
    private readonly Dictionary<string, T?> _map = new();
    private readonly Func<T, bool> _isLive;
    private readonly int _max;

    // `max` bounds the entries (cleared when full); 0 = unbounded, for keys from a small fixed set.
    public LiveCache(Func<T, bool> isLive, int max = 0)
    {
        _isLive = isLive;
        _max = max;
    }

    public int Count => _map.Count;

    // The live cached value for `key`, else `load(key)` (cached, null included).
    public T? Get(string key, Func<string, T?> load)
    {
        if (_map.TryGetValue(key, out var hit) && (hit == null || IsLive(hit))) return hit;
        var value = load(key);
        if (_max > 0 && _map.Count >= _max && !_map.ContainsKey(key)) _map.Clear();
        _map[key] = value;
        return value;
    }

    public void Clear() => _map.Clear();

    private bool IsLive(T value)
    {
        try { return _isLive(value); }
        catch { return false; }
    }
}
