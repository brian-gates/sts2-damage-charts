using System.Collections.Generic;
using Xunit;
using STS2_DamageCharts.Common;

// The 1.6.2 end-of-combat summary kept a texture the game had disposed at the room transition (an orb icon cached
// in the previous fight). Assigning it threw every frame, so the summary kept the previous fight's lines.
public class LiveCacheTests
{
    private sealed class Res { public bool Disposed; public readonly int Gen; public Res(int gen) { Gen = gen; } }

    [Fact]
    public void Hit_IsReturnedWithoutReloading_WhileLive()
    {
        int loads = 0;
        var cache = new LiveCache<Res>(r => !r.Disposed);
        var a = cache.Get("res://images/orbs/lightning_orb.png", _ => new Res(++loads));
        var b = cache.Get("res://images/orbs/lightning_orb.png", _ => new Res(++loads));
        Assert.Same(a, b);
        Assert.Equal(1, loads);
    }

    [Fact]
    public void DisposedHit_IsLoadedAgain_NeverHandedOut()
    {
        int loads = 0;
        var cache = new LiveCache<Res>(r => !r.Disposed);
        var first = cache.Get("icon", _ => new Res(++loads))!;
        first.Disposed = true;   // the game's AssetCache.UnloadMissedCacheAssets between rooms
        var second = cache.Get("icon", _ => new Res(++loads))!;
        Assert.NotSame(first, second);
        Assert.False(second.Disposed);
        Assert.Equal(2, loads);
        Assert.Same(second, cache.Get("icon", _ => new Res(++loads)));
    }

    [Fact]
    public void ThrowingLivenessCheck_CountsAsDead()
    {
        int loads = 0;
        bool broken = false;
        var cache = new LiveCache<Res>(r => broken ? throw new System.ObjectDisposedException("tex") : true);
        cache.Get("icon", _ => new Res(++loads));
        broken = true;
        cache.Get("icon", _ => new Res(++loads));
        Assert.Equal(2, loads);
    }

    [Fact]
    public void CachedMiss_IsNotProbedAgain()
    {
        int loads = 0;
        var cache = new LiveCache<Res>(r => !r.Disposed);
        Assert.Null(cache.Get("res://missing.png", _ => { loads++; return null; }));
        Assert.Null(cache.Get("res://missing.png", _ => { loads++; return null; }));
        Assert.Equal(1, loads);
    }

    [Fact]
    public void Bounded_ClearsWhenFull()
    {
        var cache = new LiveCache<Res>(r => true, max: 2);
        cache.Get("a", _ => new Res(1));
        cache.Get("b", _ => new Res(2));
        cache.Get("c", _ => new Res(3));
        Assert.Equal(1, cache.Count);
    }
}
