using System.Collections.Generic;
using Xunit;
using STS2_DamageCharts.Tracking;

public class BufferCandidatesTests
{
    private sealed class P { }

    private static BufferCandidates<P, string> New() => new();

    [Fact]
    public void AStackLostConfirmsTheHit()
    {
        var c = New(); var a = new P();
        c.HitStarted(a);
        c.Add(a, 1, 10, "hit");
        Assert.Equal(new[] { "hit" }, c.Check(_ => 0, 10, 30));
        Assert.Equal(0, c.Count);
    }

    [Fact]
    public void ADecrementThatLandsLaterStillConfirmsWithinTheWindow()
    {
        var c = New(); var a = new P();
        c.Add(a, 2, 10, "hit");
        int stacks = 2;
        Assert.Empty(c.Check(_ => stacks, 12, 30));
        stacks = 1;
        Assert.Equal(new[] { "hit" }, c.Check(_ => stacks, 20, 30));
    }

    [Fact]
    public void AnUnprovenCandidateExpires()
    {
        var c = New(); var a = new P();
        c.Add(a, 1, 10, "hit");
        Assert.Empty(c.Check(_ => 1, 41, 30));
        Assert.Equal(0, c.Count);
        Assert.Empty(c.Check(_ => 0, 42, 30));   // a later decrement credits nothing
    }

    [Fact]
    public void TheNextHitDropsAnUnconfirmedCandidate()
    {
        // Review C1: hit 1 is zeroed by something else (Buffer untouched), hit 2 is the one Buffer cancels.
        var c = New(); var a = new P();
        c.HitStarted(a);
        c.Add(a, 1, 10, "rod-zeroed hit");
        Assert.Empty(c.Check(_ => 1, 10, 30));
        c.HitStarted(a);
        c.Add(a, 1, 14, "buffered hit");
        Assert.Equal(new[] { "buffered hit" }, c.Check(_ => 0, 14, 30));
    }

    [Fact]
    public void OneStackConfirmsAtMostOneCandidate()
    {
        // Two candidates queued without a hit start between them (both read Buffer 1): only one stack was lost.
        var c = New(); var a = new P();
        c.Add(a, 1, 10, "older");
        c.Add(a, 1, 12, "newer");
        Assert.Equal(new[] { "newer" }, c.Check(_ => 0, 12, 30));
        Assert.Equal(0, c.Count);             // the other is settled, never credited later
    }

    [Fact]
    public void TwoStacksLostConfirmTwo()
    {
        var c = New(); var a = new P();
        c.Add(a, 2, 10, "first");
        c.Add(a, 2, 11, "second");
        Assert.Equal(new[] { "first", "second" }, c.Check(_ => 0, 11, 30));
    }

    [Fact]
    public void PlayersAreIndependent()
    {
        var c = New(); var a = new P(); var b = new P();
        c.Add(a, 1, 10, "a");
        c.Add(b, 1, 10, "b");
        var stacks = new Dictionary<P, int> { [a] = 1, [b] = 0 };
        Assert.Equal(new[] { "b" }, c.Check(p => stacks[p], 10, 30));
        Assert.Equal(1, c.Count);
        c.HitStarted(b);                      // b's hit start doesn't touch a's candidate
        Assert.Equal(1, c.Count);
    }

    [Fact]
    public void ZeroStacksQueueNothing()
    {
        var c = New(); var a = new P();
        c.Add(a, 0, 10, "hit");
        Assert.Equal(0, c.Count);
    }
}
