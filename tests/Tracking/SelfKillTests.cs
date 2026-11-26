using Xunit;
using STS2_DamageCharts.Tracking;

public class SelfKillTests
{
    private sealed class C { }   // stands in for a game Creature (compared by reference)
    private static readonly C Giant = new(), GasBomb = new(), Other = new();
    private const int Round = 4;
    private const long Ttl = 600;

    [Fact]
    public void WaterfallGiant_ExplodingOnItsOwnMove_IsNotCredited()
    {
        // TriggerAboutToBlowState set its HP to 999,999,999; ExplodeMove then calls CreatureCmd.Kill on itself.
        Assert.True(SelfKill.IsSelfKill(Giant, mover: Giant, moverRound: Round, round: Round, enemyTurn: true,
            byCard: false, playerSourceFresh: false, hpBefore: 999_999_999));
        Assert.False(SelfKill.Credit(tagged: false, selfKillFrame: 100, frame: 101, ttl: Ttl));
    }

    [Fact]
    public void GasBomb_ExplodingOnItsOwnMove_IsNotCredited()
    {
        Assert.True(SelfKill.IsSelfKill(GasBomb, GasBomb, Round, Round, enemyTurn: true, byCard: false,
            playerSourceFresh: false, hpBefore: 7));
        Assert.False(SelfKill.Credit(tagged: false, selfKillFrame: 50, frame: 50, ttl: Ttl));
    }

    [Fact]
    public void DoomTaggedKill_IsStillCredited_EvenOnTheDoomedMonstersOwnTurn()
    {
        // DoomKillPrefix tags the creature before DoomKill calls CreatureCmd.Kill; the tag wins over the mark.
        Assert.True(SelfKill.Credit(tagged: true, selfKillFrame: 10, frame: 10, ttl: Ttl));
    }

    [Fact]
    public void FreshPlayerSourceKill_IsNotMarked_AndIsCredited()
    {
        Assert.False(SelfKill.IsSelfKill(Giant, Giant, Round, Round, enemyTurn: true, byCard: false,
            playerSourceFresh: true, hpBefore: 30));
        Assert.True(SelfKill.Credit(tagged: false, selfKillFrame: null, frame: 10, ttl: Ttl));
    }

    [Fact]
    public void OrdinaryTaggedDamage_IsUnaffected()
    {
        Assert.True(SelfKill.Credit(tagged: true, selfKillFrame: null, frame: 10, ttl: Ttl));
        Assert.True(SelfKill.Credit(tagged: false, selfKillFrame: null, frame: 10, ttl: Ttl)); // Status fallback stays
    }

    [Fact]
    public void KillOfAnotherMonster_IsNotASelfKill() =>
        Assert.False(SelfKill.IsSelfKill(Other, Giant, Round, Round, true, false, false, 20));

    [Fact]
    public void KillOnThePlayersTurn_IsNotASelfKill() =>
        Assert.False(SelfKill.IsSelfKill(Giant, Giant, Round, Round, enemyTurn: false, false, false, 20));

    [Fact]
    public void MoverFromAnEarlierRound_IsStale() =>
        // Start of the next enemy turn, before any monster moves: last round's last mover must not count.
        Assert.False(SelfKill.IsSelfKill(Giant, Giant, moverRound: Round - 1, round: Round, true, false, false, 20));

    [Fact]
    public void CardKill_IsNotASelfKill() =>
        Assert.False(SelfKill.IsSelfKill(Giant, Giant, Round, Round, true, byCard: true, false, 20));

    [Fact]
    public void AlreadyDead_KillChangesNoHp_SoNoMark() =>
        // A Damage hit already took it to 0 (credited by its tag); the follow-up Kill must not leave a mark.
        Assert.False(SelfKill.IsSelfKill(Giant, Giant, Round, Round, true, false, false, hpBefore: 0));

    [Fact]
    public void NoMover_IsNotASelfKill() =>
        Assert.False(SelfKill.IsSelfKill<C>(Giant, null, Round, Round, true, false, false, 20));

    [Fact]
    public void StaleMark_Expires() =>
        Assert.True(SelfKill.Credit(tagged: false, selfKillFrame: 10, frame: 10 + Ttl + 1, ttl: Ttl));
}
