using System.Collections.Generic;
using STS2_DamageCharts;
using Xunit;

public class DamageForecastTests
{
    private static ForecastItem Attack(int perHit, int hits = 1) =>
        new("E", ForecastKind.Attack, hits, perHit, blockable: true, conditional: false);
    private static ForecastItem Dot(int amount) =>
        new("Bleed", ForecastKind.Dot, 1, amount, blockable: false, conditional: false);
    private static ForecastItem SelfCard(int amount) =>
        new("Offering", ForecastKind.SelfCard, 1, amount, blockable: false, conditional: true);

    private static ForecastResult Run(int hp, int maxHp, int block, bool hidden, params ForecastItem[] items) =>
        DamageForecast.Compute(new ForecastInputs(hp, maxHp, block, 0, hidden, new List<ForecastItem>(items)));

    private static ForecastResult RunOsty(int hp, int block, int ostyAbsorb, params ForecastItem[] items) =>
        DamageForecast.Compute(new ForecastInputs(hp, 50, block, ostyAbsorb, false, new List<ForecastItem>(items)));

    [Fact]
    public void SingleAttackNoBlock_LossEqualsDamage()
    {
        var r = Run(40, 50, 0, false, Attack(12));
        Assert.Equal(12, r.Incoming);
        Assert.Equal(0, r.Mitigated);
        Assert.Equal(12, r.MinLoss);
        Assert.Equal(12, r.MaxLoss);
        Assert.False(r.IsSafe);
        Assert.False(r.IsLethal);
    }

    [Fact]
    public void AttackFullyBlocked_IsSafe()
    {
        var r = Run(40, 50, 12, false, Attack(12));
        Assert.Equal(12, r.Mitigated);
        Assert.Equal(0, r.MinLoss);
        Assert.Equal(0, r.MaxLoss);
        Assert.True(r.IsSafe);
    }

    [Fact]
    public void MultiHitPartialBlock_BlockAbsorbsPooledTotal()
    {
        // 2x6 = 12 incoming, 10 block -> 10 absorbed, 2 through.
        var r = Run(40, 50, 10, false, Attack(6, 2));
        Assert.Equal(12, r.Incoming);
        Assert.Equal(10, r.Mitigated);
        Assert.Equal(2, r.MinLoss);
        Assert.Equal(2, r.MaxLoss);
    }

    [Fact]
    public void ExcessBlockDoesNotOverMitigate()
    {
        var r = Run(40, 50, 20, false, Attack(6));
        Assert.Equal(6, r.Mitigated);
        Assert.Equal(0, r.MinLoss);
        Assert.True(r.IsSafe);
    }

    [Fact]
    public void DotBypassesBlock_AndBlocksAllAttacks()
    {
        // 6 attack fully blocked by 10; 3 bleed ignores block.
        var r = Run(40, 50, 10, false, Attack(6), Dot(3));
        Assert.Equal(9, r.Incoming);   // 6 attack + 3 dot
        Assert.Equal(6, r.Mitigated);  // block only absorbs the attack
        Assert.Equal(3, r.MinLoss);    // bleed still lands
        Assert.Equal(3, r.MaxLoss);
        Assert.False(r.IsSafe);
    }

    [Fact]
    public void SelfDamageCard_OnlyWidensMax()
    {
        // 6 attack blocked; Offering (self 6) is conditional -> min 0, max 6.
        var r = Run(40, 50, 10, false, Attack(6), SelfCard(6));
        Assert.Equal(0, r.MinLoss);
        Assert.Equal(6, r.MaxLoss);
        Assert.False(r.IsSafe);
    }

    [Fact]
    public void HiddenIntent_NotSafeEvenAtZeroLoss()
    {
        var r = Run(40, 50, 0, true);
        Assert.Equal(0, r.MinLoss);
        Assert.Equal(0, r.MaxLoss);
        Assert.True(r.HasHidden);
        Assert.False(r.IsSafe);
    }

    [Fact]
    public void Lethal_WhenMaxLossReachesCurrentHp()
    {
        var r = Run(5, 50, 0, false, Attack(8));
        Assert.True(r.IsLethal);
    }

    [Fact]
    public void OstyAbsorbsAttack_NoBlock_NoLoss()
    {
        // 8 attack, no block, Osty pool 10 -> Osty eats it all, 0 loss, safe.
        var r = RunOsty(40, 0, 10, Attack(8));
        Assert.Equal(8, r.Mitigated);
        Assert.Equal(0, r.MinLoss);
        Assert.True(r.IsSafe);
    }

    [Fact]
    public void OstyPlusBlock_Pooled_OverflowCarries()
    {
        // 20 attack; block 5 + Osty 10 = 15 pool -> 5 overflow to HP.
        var r = RunOsty(40, 5, 10, Attack(20));
        Assert.Equal(15, r.Mitigated);
        Assert.Equal(5, r.MinLoss);
        Assert.Equal(10, r.OstyAbsorb);
    }

    [Fact]
    public void OstyDoesNotAbsorbDots()
    {
        // 6 attack fully eaten by Osty 10; 3 bleed bypasses Osty -> 3 loss.
        var r = RunOsty(40, 0, 10, Attack(6), Dot(3));
        Assert.Equal(3, r.MinLoss);
        Assert.False(r.IsSafe);
    }

    [Fact]
    public void ConditionalAttack_AbsorbedByLeftoverBlockFirst()
    {
        // guaranteed 4 attack, block 10 -> 4 absorbed, 6 block left.
        // conditional attack 10 -> 6 absorbed by leftover, 4 through -> maxLoss 4.
        var conditionalAttack = new ForecastItem("E2", ForecastKind.Attack, 1, 10, blockable: true, conditional: true);
        var r = Run(40, 50, 10, false, Attack(4), conditionalAttack);
        Assert.Equal(0, r.MinLoss);
        Assert.Equal(4, r.MaxLoss);
    }

    [Fact]
    public void EndTurnBlock_AbsorbsAttacksLikeBlock()
    {
        // 5 Plating, no block now, enemy attacks 2x8: Plating lands before the enemy turn.
        var r = DamageForecast.Compute(new ForecastInputs(58, 74, 0, 0, false,
            new List<ForecastItem> { Attack(8, 2) }, endTurnBlock: 5));
        Assert.Equal(16, r.Incoming);
        Assert.Equal(5, r.Mitigated);
        Assert.Equal(11, r.MinLoss);
        Assert.Equal(5, r.Block);
    }

    [Fact]
    public void EndTurnBlock_DoesNotReduceUnblockable()
    {
        var r = DamageForecast.Compute(new ForecastInputs(40, 50, 0, 0, false,
            new List<ForecastItem> { Dot(6) }, endTurnBlock: 5));
        Assert.Equal(6, r.MinLoss);
    }

    [Fact]
    public void Overblock_IsBlockLeftAfterAttacks()
    {
        var r = Run(40, 50, 20, false, Attack(6, 2));
        Assert.True(r.IsSafe);
        Assert.Equal(8, r.Overblock);
    }

    [Fact]
    public void Overblock_ZeroWhenBlockAllNeededOrShort()
    {
        Assert.Equal(0, Run(40, 50, 12, false, Attack(12)).Overblock);
        Assert.Equal(0, Run(40, 50, 5, false, Attack(12)).Overblock);
    }

    [Fact]
    public void Overblock_CountsConditionalAttacksAsWorstCase()
    {
        var cond = new ForecastItem("E", ForecastKind.Attack, 1, 5, blockable: true, conditional: true);
        var r = Run(40, 50, 20, false, Attack(10), cond);
        Assert.Equal(5, r.Overblock);
    }

    [Fact]
    public void Overblock_IgnoresUnblockableAndNeverExceedsOwnBlock()
    {
        Assert.Equal(10, Run(40, 50, 10, false, Dot(4)).Overblock);
        // Osty covers the attack, but spare Osty isn't block: overblock caps at the player's block.
        Assert.Equal(5, RunOsty(40, 5, 30, Attack(10)).Overblock);
    }

    // Osty with an explicit HP / block split (absorb = hp + block).
    private static ForecastResult RunOstySplit(int block, int ostyHp, int ostyBlock, params ForecastItem[] items) =>
        DamageForecast.Compute(new ForecastInputs(40, 50, block, ostyHp + ostyBlock, false, new List<ForecastItem>(items), 0, ostyHp));
    private static ForecastItem CondAttack(int perHit) =>
        new("E", ForecastKind.Attack, 1, perHit, blockable: true, conditional: true);

    [Fact]
    public void OstyLoss_NoOsty_IsZero()
    {
        var r = Run(40, 50, 0, false, Attack(12));
        Assert.Equal(0, r.OstyHp);
        Assert.Equal(0, r.OstyHpLoss);
        Assert.False(r.OstyDies);
    }

    [Fact]
    public void OstyLoss_PlayerBlockCoversEverything_OstyUntouched()
    {
        var r = RunOstySplit(10, 11, 0, Attack(8));
        Assert.Equal(0, r.OstyHpLoss);
        Assert.False(r.OstyDies);
    }

    [Fact]
    public void OstyLoss_OstyFullyCovers_LosesOnlyWhatGetsPastBothBlocks()
    {
        // 20 attack: player block 5, Osty block 4, so Osty loses 11 of its 15 HP; the player loses nothing.
        var r = RunOstySplit(5, 15, 4, Attack(20));
        Assert.Equal(11, r.OstyHpLoss);
        Assert.False(r.OstyDies);
        Assert.Equal(0, r.MaxLoss);
    }

    [Fact]
    public void OstyLoss_Partial()
    {
        var r = RunOstySplit(0, 11, 0, Attack(6));
        Assert.Equal(6, r.OstyHpLoss);
        Assert.False(r.OstyDies);
    }

    [Fact]
    public void OstyLoss_Dies_CappedAtHp_OverflowToPlayer()
    {
        // 3 x 7 = 21 vs block 2 + Osty 11 HP: Osty loses all 11 and dies, 8 reaches the player.
        var r = RunOstySplit(2, 11, 0, Attack(7, hits: 3));
        Assert.Equal(11, r.OstyHpLoss);
        Assert.True(r.OstyDies);
        Assert.Equal(8, r.MinLoss);
    }

    [Fact]
    public void OstyLoss_ExactlyHp_Dies()
    {
        Assert.True(RunOstySplit(0, 6, 0, Attack(6)).OstyDies);
    }

    [Fact]
    public void OstyLoss_ConditionalAttacksCountTowardWorstCase()
    {
        // Guaranteed 4 alone would cost Osty 4; a conditional 10 makes the worst case lethal.
        var r = RunOstySplit(0, 11, 0, Attack(4), CondAttack(10));
        Assert.Equal(11, r.OstyHpLoss);
        Assert.True(r.OstyDies);
    }

    [Fact]
    public void OstyLoss_DotsBypassOsty()
    {
        var r = RunOstySplit(0, 11, 0, Dot(5));
        Assert.Equal(0, r.OstyHpLoss);
    }

    [Fact]
    public void OstyLoss_UnknownSplit_TreatsAbsorbAsHp()
    {
        var r = RunOsty(40, 0, 10, Attack(4));
        Assert.Equal(10, r.OstyHp);
        Assert.Equal(4, r.OstyHpLoss);
    }
}
