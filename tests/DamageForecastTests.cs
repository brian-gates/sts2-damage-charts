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
}
