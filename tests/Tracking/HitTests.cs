using System.Linq;
using Xunit;
using STS2_DamageCharts.Tracking;

public class HitTests
{
    private static string Name(int slot) => $"P{slot}";

    [Fact]
    public void Text_MatchesTodaysLogLines()
    {
        Assert.Equal("Strike → Cultist  6",
            new Hit(1, HitKind.Dealt, 0, "Strike", "CARD.STRIKE_SILENT", 0, "Cultist", -1, 6).Text(Name));
        Assert.Equal("Cultist → you  9",
            new Hit(1, HitKind.Taken, 0, "Cultist", null, -1, "you", 0, 9).Text(Name));
        Assert.Equal("Cultist → Osty  4",
            new Hit(2, HitKind.Absorbed, 1, "Cultist", null, -1, "Osty", -1, 4).Text(Name));
        Assert.Equal("you healed  7",
            new Hit(2, HitKind.Healed, 0, null, null, -1, "you", 0, 7).Text(Name));
        Assert.Equal("Fire Potion used",
            new Hit(3, HitKind.Potion, 0, "Fire Potion", null, -1, null, -1, -1).Text(Name));
        Assert.Equal("Fire Potion used → Cultist",
            new Hit(3, HitKind.Potion, 0, "Fire Potion", null, -1, "Cultist", -1, -1).Text(Name));
        Assert.Equal("Blood Potion used → Ironclad",
            new Hit(3, HitKind.Potion, 0, "Blood Potion", null, -1, "Ironclad", 1, -1).Text(Name));
    }

    [Fact]
    public void Text_FallsBackToPlayerNameWhenNoNameStored()
    {
        Assert.Equal("Cultist → P1  9", new Hit(1, HitKind.Taken, 1, "Cultist", null, -1, null, 1, 9).Text(Name));
    }

    private static DamageTracker Tracker()
    {
        var t = new DamageTracker();
        t.Reset(1, new[] { "Silent" }, 0);
        return t;
    }

    private static Hit Dealt(int i) => new(1, HitKind.Dealt, 0, "S" + i, null, 0, "E", -1, i);

    [Fact]
    public void Tracker_KeepsFirst600AndFlagsTruncation()
    {
        var t = Tracker();
        for (int i = 0; i < 600; i++) t.AddHit(Dealt(i));
        var snap = t.SourceSnapshot();
        Assert.Equal(600, snap.FightHits.Count);
        Assert.False(snap.HitsTruncated);
        t.AddHit(Dealt(600));
        snap = t.SourceSnapshot();
        Assert.Equal(600, snap.FightHits.Count);
        Assert.True(snap.HitsTruncated);
        Assert.Equal(0, snap.FightHits[0].Amount);
        Assert.Equal(599, snap.FightHits[^1].Amount);
    }

    [Fact]
    public void Tracker_DisplayLogStillCappedAt500()
    {
        var t = Tracker();
        for (int i = 0; i < 700; i++) t.AddHit(Dealt(i));
        var snap = t.SourceSnapshot();
        Assert.Equal(300, snap.Log.Length); // the panel shows the latest 300 of the 500 kept
        Assert.Equal("S699 → E  699", snap.Log[^1].Text);
        Assert.Equal("S400 → E  400", snap.Log[0].Text);
        Assert.All(snap.Log, e => Assert.False(e.Taken));
    }

    [Fact]
    public void Tracker_TakenFlagCoversTakenAndAbsorbedOnly()
    {
        var t = Tracker();
        t.AddHit(new Hit(1, HitKind.Taken, 0, "A", null, -1, "you", 0, 1));
        t.AddHit(new Hit(1, HitKind.Absorbed, 0, "A", null, -1, "Pet", -1, 1));
        t.AddHit(new Hit(1, HitKind.Healed, 0, null, null, -1, "you", 0, 1));
        t.AddHit(new Hit(1, HitKind.Potion, 0, "P", null, -1, null, -1, -1));
        Assert.Equal(new[] { true, true, false, false }, t.SourceSnapshot().Log.Select(e => e.Taken).ToArray());
    }

    [Fact]
    public void Reset_ClearsHits()
    {
        var t = Tracker();
        for (int i = 0; i < 601; i++) t.AddHit(Dealt(i));
        t.Reset(1, new[] { "Silent" }, 0);
        var snap = t.SourceSnapshot();
        Assert.Empty(snap.FightHits);
        Assert.False(snap.HitsTruncated);
        Assert.Empty(snap.Log);
    }
}
