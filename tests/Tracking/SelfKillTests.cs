using Xunit;
using STS2_DamageCharts.Tracking;

public class SelfKillTests
{
    // ----- Kill time: is this the monster killing itself on its own move? -----

    [Fact]
    public void WaterfallGiant_ExplodingOnItsOwnMove_IsASelfKill() =>
        // TriggerAboutToBlowState set its HP to 999,999,999; ExplodeMove then calls CreatureCmd.Kill on itself.
        Assert.True(SelfKill.IsSelfKill(performingOwnMove: true, byCard: false, playerSourceFresh: false, hpBefore: 999_999_999));

    [Fact]
    public void GasBomb_ExplodingOnItsOwnMove_IsASelfKill() =>
        Assert.True(SelfKill.IsSelfKill(performingOwnMove: true, byCard: false, playerSourceFresh: false, hpBefore: 7));

    [Fact]
    public void KillOfAMonsterNotPerformingAMove_IsNotASelfKill() =>
        // Doom at turn end, another monster's move, a player's turn: IsPerformingMove is false on the killed monster.
        Assert.False(SelfKill.IsSelfKill(performingOwnMove: false, byCard: false, playerSourceFresh: false, hpBefore: 20));

    [Fact]
    public void FreshPlayerSource_IsNotASelfKill() =>
        Assert.False(SelfKill.IsSelfKill(performingOwnMove: true, byCard: false, playerSourceFresh: true, hpBefore: 30));

    [Fact]
    public void CardKill_IsNotASelfKill() =>
        Assert.False(SelfKill.IsSelfKill(performingOwnMove: true, byCard: true, playerSourceFresh: false, hpBefore: 20));

    [Fact]
    public void AlreadyDead_KillChangesNoHp_SoNoMark() =>
        Assert.False(SelfKill.IsSelfKill(performingOwnMove: true, byCard: false, playerSourceFresh: false, hpBefore: 0));

    // ----- HP-loss time: credit or not -----

    [Fact]
    public void SelfKillMark_NoTag_IsNotCredited() =>
        Assert.False(SelfKill.Credit(HpTag.None, selfKillNow: true));

    [Fact]
    public void SelfKillMark_BeatsAStaleDamageTag() =>
        // A fully blocked hit left its Damage tag on the Giant; the Kill's ~999,999,999 must not go to that card.
        Assert.False(SelfKill.Credit(HpTag.Damage, selfKillNow: true));

    [Fact]
    public void DoomKillTag_NoMark_IsCredited() =>
        // Doom kills are not the monster's move, so there is no mark; DoomKillPrefix's tag credits the applier.
        Assert.True(SelfKill.Credit(HpTag.DoomKill, selfKillNow: false));

    [Fact]
    public void DoomKillTag_WinsEvenIfMarked() =>
        Assert.True(SelfKill.Credit(HpTag.DoomKill, selfKillNow: true));

    [Fact]
    public void NoTagNoMark_KeepsTheStatusFallback() =>
        Assert.True(SelfKill.Credit(HpTag.None, selfKillNow: false));

    [Fact]
    public void OrdinaryTaggedDamage_IsUnaffected() =>
        Assert.True(SelfKill.Credit(HpTag.Damage, selfKillNow: false));

    [Fact]
    public void Mark_AppliesOnlyInItsOwnFrame()
    {
        Assert.True(SelfKill.MarkedNow(markFrame: 50, frame: 50));
        Assert.False(SelfKill.MarkedNow(markFrame: 50, frame: 51));
        Assert.False(SelfKill.MarkedNow(markFrame: null, frame: 50));
    }
}
