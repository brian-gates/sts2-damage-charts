using Xunit;
using STS2_DamageCharts.Tracking;

public class LogTextTests
{
    private static string Name(int slot) => $"P{slot}";

    private static (string, string?) F(Hit h, bool coop = false, bool showTarget = true) => LogText.Format(h, Name, coop, showTarget);

    [Fact]
    public void Taken_DropsTheLocalTargetInSinglePlayerOnly()
    {
        var h = new Hit(1, HitKind.Taken, 0, "Leaf Slime (M)", null, -1, "you", 0, 8);
        Assert.Equal(("Leaf Slime (M)", "8"), F(h));
        Assert.Equal(("Leaf Slime (M) → you", "8"), F(h, coop: true));
        var byAlly = new Hit(1, HitKind.Taken, 1, "Ironclad", null, 0, "Silent", 1, 3);
        Assert.Equal(("Ironclad → Silent", "3"), F(byAlly, coop: true));
    }

    [Fact]
    public void Absorbed_AlwaysNamesThePet()
    {
        Assert.Equal(("Cultist → Osty", "4"), F(new Hit(1, HitKind.Absorbed, 0, "Cultist", null, -1, "Osty", -1, 4)));
    }

    [Fact]
    public void Buffered_SaysSaved()
    {
        var h = new Hit(1, HitKind.Buffered, 0, "Twig Slime (M)", null, -1, "you", 0, 11);
        Assert.Equal(("Twig Slime (M)", "11 saved"), F(h));
        Assert.Equal(("Twig Slime (M) → you", "11 saved"), F(h, coop: true));
        Assert.Equal(("?", "7 saved"), F(new Hit(1, HitKind.Buffered, 0, null, null, -1, "you", 0, 7)));
    }

    [Fact]
    public void Healed_IsJustTheAmountUnlessCoop()
    {
        var h = new Hit(1, HitKind.Healed, 0, null, null, -1, "Silent", 0, 6);
        Assert.Equal(("", "+6"), F(h));
        Assert.Equal(("Silent", "+6"), F(h, coop: true));
    }

    [Fact]
    public void Dealt_DropsTheTargetWithOneEnemy()
    {
        var h = new Hit(1, HitKind.Dealt, 0, "Strike", null, 0, "Cultist", -1, 6);
        Assert.Equal(("Strike → Cultist", "6"), F(h));
        Assert.Equal(("Strike", "6"), F(h, showTarget: false));
    }

    [Fact]
    public void Potion_KeepsTheFullLine()
    {
        var h = new Hit(1, HitKind.Potion, 0, "Fire Potion", null, -1, "Cultist", -1, -1);
        Assert.Equal(("Fire Potion used → Cultist", null), F(h));
    }

    [Fact]
    public void SingleDealtTarget_LooksAtDealtLinesOnly()
    {
        LogEntry D(string target) => new(1, "x", LogTone.Plain, HitKind.Dealt, 1, target: target);
        var taken = new LogEntry(1, "x", LogTone.Taken, HitKind.Taken, 1, target: "you");
        Assert.True(LogText.SingleDealtTarget(new[] { D("Cultist"), taken, D("Cultist") }));
        Assert.False(LogText.SingleDealtTarget(new[] { D("Cultist"), D("Louse") }));
        Assert.True(LogText.SingleDealtTarget(new LogEntry[0]));
    }

    [Fact]
    public void Tracker_UsesThePlayerCount()
    {
        var t = new DamageTracker();
        t.Reset(2, new[] { "Silent (You)", "Ironclad" }, 0);
        t.AddHit(new Hit(1, HitKind.Taken, 0, "Cultist", null, -1, "Silent", 0, 9));
        t.AddHit(new Hit(1, HitKind.Dealt, 0, "Strike", null, 0, "Cultist", -1, 6));
        var log = t.SourceSnapshot().Log;
        Assert.Equal("Cultist → Silent", log[0].Lead);
        Assert.Equal("Strike → Cultist", log[1].Lead);
        Assert.Equal("Strike", log[1].ShortLead);
        Assert.Equal("Cultist", log[1].Target);
    }
}
