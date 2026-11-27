using System.Collections.Generic;
using Xunit;
using STS2_DamageCharts.Forecast;

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

    private static ForecastResult RunWuppy(int hp, int block, int wuppyHp, int wuppyBlock, params ForecastItem[] items) =>
        DamageForecast.Compute(new ForecastInputs(hp, 50, block, 0, false, new List<ForecastItem>(items),
            wuppyHp: wuppyHp, wuppyBlock: wuppyBlock));

    // Wuppy (WuppysLoyaltyPower.ModifyPreBlockDamageTarget) takes the whole attack before block applies.
    [Fact]
    public void WuppyTakesTheAttack_PlayerBlockUntouched()
    {
        var r = RunWuppy(40, 6, 20, 0, Attack(8));
        Assert.Equal(0, r.MaxLoss);
        Assert.True(r.IsSafe);
        Assert.Equal(6, r.Overblock);   // the player's block is all spare
        Assert.Equal(8, r.Mitigated);
        Assert.Equal(20, r.WuppyAbsorb);
        Assert.Equal(8, r.WuppyHpLoss);
        Assert.False(r.WuppyDies);
    }

    [Fact]
    public void WuppyBlockSoaksBeforeItsHp()
    {
        var r = RunWuppy(40, 0, 5, 4, Attack(8));
        Assert.Equal(0, r.MaxLoss);
        Assert.Equal(9, r.WuppyAbsorb);
        Assert.Equal(4, r.WuppyHpLoss);
        Assert.False(r.WuppyDies);
    }

    [Fact]
    public void TheHitThatKillsWuppyIsSpent_LaterHitsReachThePlayer()
    {
        // Wuppy 5: hit 1 (8) is aimed at Wuppy and kills it, its overflow goes nowhere; hit 2 (8) meets
        // the player's 3 block, then 5 HP.
        var r = RunWuppy(40, 3, 5, 0, Attack(8, hits: 2));
        Assert.Equal(5, r.MaxLoss);
        Assert.Equal(16, r.Incoming);
        Assert.Equal(11, r.Mitigated);  // 8 to Wuppy + 3 block
        Assert.True(r.WuppyDies);
        Assert.Equal(0, r.Overblock);
    }

    [Fact]
    public void WuppyIgnoresDotsAndSelfDamage()
    {
        var r = RunWuppy(40, 0, 20, 0, Dot(3), SelfCard(4));
        Assert.Equal(3, r.MinLoss);
        Assert.Equal(7, r.MaxLoss);
        Assert.Equal(0, r.WuppyHpLoss);
    }

    [Fact]
    public void WuppyGoesFirst_OstyAndBufferUntouched()
    {
        var r = DamageForecast.Compute(new ForecastInputs(40, 50, 0, 10, false, new List<ForecastItem> { Attack(6), Dot(2) },
            ostyHp: 10, buffer: 1, wuppyHp: 20));
        Assert.Equal(0, r.OstyHpLoss);
        Assert.Equal(6, r.WuppyHpLoss);
        Assert.Equal(0, r.MaxLoss);     // Buffer is still there for the Dot
    }

    [Fact]
    public void NoWuppy_NothingChanges()
    {
        var r = RunWuppy(40, 0, 0, 7, Attack(8)); // block on a dead Wuppy is ignored
        Assert.Equal(8, r.MaxLoss);
        Assert.Equal(0, r.WuppyAbsorb);
    }

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
        // The player's block soaks first (Osty only gets what's past it), so 5 block vs a 10 attack is all
        // used: no spare block, even though Osty has plenty left.
        Assert.Equal(0, RunOsty(40, 5, 30, Attack(10)).Overblock);
        // Spare block never includes Osty: 15 block vs 10 leaves 5, whatever Osty has.
        Assert.Equal(5, RunOsty(40, 15, 30, Attack(10)).Overblock);
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

    [Fact]
    public void HeldTakeDamageCard_IsAbsorbedByBlock()
    {
        // Infection held (take 3 damage, blockable) plus a 9 attack against 12 block: nothing gets through.
        var infection = new ForecastItem("Infection", ForecastKind.Dot, 1, 3, blockable: true, conditional: false);
        var r = Run(32, 66, 12, false, Attack(9), infection);
        Assert.Equal(0, r.MaxLoss);
        Assert.True(r.IsSafe);
    }

    private static ForecastItem SelfTake(int amount) =>
        new("Disintegration", ForecastKind.Dot, 1, amount, blockable: true, conditional: false);

    [Fact]
    public void SelfDamage_IsNotAbsorbedByOsty()
    {
        // Disintegration (take 6 at end of turn) with no block: Osty can't take it for the Necrobinder.
        var r = RunOsty(30, 0, 11, SelfTake(6));
        Assert.Equal(6, r.MinLoss);
        Assert.False(r.IsSafe);
    }

    [Fact]
    public void SelfDamage_SpendsBlockBeforeEnemyAttacks()
    {
        // 6 block: Disintegration eats it, so the 8 attack goes to Osty (11): player loses 0, Osty 8.
        var r = RunOsty(30, 6, 11, SelfTake(6), Attack(8));
        Assert.Equal(0, r.MaxLoss);
        Assert.Equal(8, r.OstyHpLoss);
        Assert.Equal(0, r.Overblock);
    }

    [Fact]
    public void SelfDamage_PartlyBlocked_RestHitsPlayer()
    {
        var r = RunOsty(30, 4, 11, SelfTake(6), Attack(5));
        Assert.Equal(2, r.MinLoss);
    }

    private static ForecastResult RunBuffer(int block, int buffer, int ostyAbsorb, params ForecastItem[] items) =>
        DamageForecast.Compute(new ForecastInputs(40, 50, block, ostyAbsorb, false, new List<ForecastItem>(items), 0, -1, buffer));

    [Fact]
    public void Buffer_CancelsAnUnblockedHit_IsSafe()
    {
        var r = RunBuffer(0, 1, 0, Attack(20));
        Assert.Equal(0, r.MaxLoss);
        Assert.True(r.IsSafe);
    }

    [Fact]
    public void Buffer_CancelsOneHitPerStack()
    {
        // 3x5, Buffer 2: two hits cancelled, the third lands.
        var r = RunBuffer(0, 2, 0, Attack(5, 3));
        Assert.Equal(5, r.MinLoss);
        Assert.False(r.IsSafe);
    }

    [Fact]
    public void Buffer_NotSpentOnFullyBlockedHits()
    {
        // 6 block covers the first 6-damage hit; Buffer 1 cancels the second.
        var r = RunBuffer(6, 1, 0, Attack(6, 2));
        Assert.Equal(0, r.MaxLoss);
        Assert.True(r.IsSafe);
    }

    [Fact]
    public void Buffer_NotSpentOnHitsOstyAbsorbs()
    {
        // Osty soaks the first hit; Buffer cancels the second.
        var r = RunBuffer(0, 1, 10, Attack(8, 2));
        Assert.Equal(0, r.MaxLoss);
        Assert.True(r.IsSafe);
    }

    [Fact]
    public void Buffer_CoversUnblockableHpLoss()
    {
        var r = RunBuffer(0, 1, 0, Dot(4));
        Assert.True(r.IsSafe);
    }

    [Fact]
    public void Buffer_SpentBySelfDamageBeforeEnemyAttacks()
    {
        // Self damage resolves first and uses the only Buffer, so the attack lands.
        var r = RunBuffer(0, 1, 0, Dot(3), Attack(10));
        Assert.Equal(10, r.MinLoss);
    }

    [Fact]
    public void Buffer_ConditionalHitCanUseItUp_WorstCaseOnly()
    {
        var r = RunBuffer(0, 1, 0, SelfCard(5), Attack(10));
        Assert.Equal(0, r.MinLoss);   // without the card, Buffer cancels the attack
        Assert.Equal(10, r.MaxLoss);  // playing it spends Buffer first
        Assert.False(r.IsSafe);
    }

    private static ForecastResult RunIntangible(int block, int buffer, params ForecastItem[] items) =>
        DamageForecast.Compute(new ForecastInputs(40, 50, block, 0, false, new List<ForecastItem>(items), 0, -1, buffer, intangible: true));

    [Fact]
    public void Intangible_CapsEachHitAtOne()
    {
        // 3 hits (the game's intent number may be uncapped if read from the raw base) and a 4-HP DoT: 1 each.
        var r = RunIntangible(0, 0, Attack(9, 3), Dot(4));
        Assert.Equal(4, r.MinLoss);
        Assert.Equal(4, r.Incoming);
    }

    [Fact]
    public void Intangible_OneBlockPerHit()
    {
        // Capped before block: 2 block covers two 1-damage hits, the third lands.
        var r = RunIntangible(2, 0, Attack(20, 3));
        Assert.Equal(2, r.Mitigated);
        Assert.Equal(1, r.MinLoss);
    }

    [Fact]
    public void Intangible_WithBuffer_IsSafe()
    {
        var r = RunIntangible(0, 1, Attack(30));
        Assert.True(r.IsSafe);
    }

    // Tungsten Rod (TungstenRod.ModifyHpLostAfterOsty): every HP loss on its owner, after block and Osty, is 1 less.
    private static ForecastResult RunRod(int block, int buffer, bool intangible, params ForecastItem[] items) =>
        DamageForecast.Compute(new ForecastInputs(40, 50, block, 0, false, new List<ForecastItem>(items), 0, -1, buffer,
            intangible, hpLossReduction: 1));

    [Fact]
    public void TungstenRod_OneLessPerHpLoss()
    {
        // The reported fight: a 13 attack and 2 Poison cost 12 + 1, not 15.
        var r = RunRod(0, 0, false, Attack(13), Dot(2));
        Assert.Equal(13, r.MinLoss);
        Assert.Equal(15, r.Incoming);   // the threat itself is unchanged
    }

    [Fact]
    public void TungstenRod_AppliesAfterBlock()
    {
        // 3 hits of 5 against 7 block: 0, 3, 5 get through; the Rod takes 1 off the two that cost HP.
        var r = RunRod(7, 0, false, Attack(5, 3));
        Assert.Equal(6, r.MinLoss);
    }

    [Fact]
    public void TungstenRod_WithIntangible_IsSafe()
    {
        // Intangible (a power) caps each hit at 1 before the Rod (a relic) takes it to 0.
        var r = RunRod(0, 0, true, Attack(30, 2), Dot(4));
        Assert.True(r.IsSafe);
    }

    [Fact]
    public void TungstenRod_KeepsBufferForARealHit()
    {
        // A 1-damage hit costs nothing with the Rod, so Buffer is still there for the 10.
        var r = RunRod(0, 1, false, Attack(1), Attack(10));
        Assert.Equal(0, r.MinLoss);
    }

    // Beating Remnant (BeatingRemnant.ModifyHpLostAfterOsty): at most 20 HP lost per turn (the player's turn and
    // the enemy turn after it), counting HP already lost this turn; Poison ticks after it resets.
    private static ForecastItem Poison(int amount) =>
        new("Poison", ForecastKind.Dot, 1, amount, blockable: false, conditional: false, nextTurnStart: true);

    private static ForecastResult RunRemnant(int lostSoFar, int block, int buffer, int reduction, bool reductionFirst,
        params ForecastItem[] items) =>
        DamageForecast.Compute(new ForecastInputs(60, 80, block, 0, false, new List<ForecastItem>(items), 0, -1, buffer,
            false, hpLossReduction: reduction, turnHpLossLimit: 20, turnHpLossSoFar: lostSoFar,
            reductionBeforeLimit: reductionFirst));

    [Fact]
    public void BeatingRemnant_CapsTheTurnAt20()
    {
        var r = RunRemnant(0, 0, 0, 0, true, Attack(15, 3));
        Assert.Equal(20, r.MinLoss);
        Assert.Equal(45, r.Incoming);
        Assert.False(r.IsLethal);
    }

    [Fact]
    public void BeatingRemnant_CountsHpAlreadyLostThisTurn()
    {
        var r = RunRemnant(12, 0, 0, 0, true, Attack(30));
        Assert.Equal(8, r.MinLoss);
    }

    [Fact]
    public void BeatingRemnant_PoisonGetsAFreshTurn()
    {
        // The enemy turn uses up the 20; Poison ticks at the start of the next turn, after the reset.
        var r = RunRemnant(0, 0, 0, 0, true, Attack(30), Poison(5));
        Assert.Equal(25, r.MinLoss);
    }

    [Fact]
    public void BeatingRemnant_LimitSpentStillCostsNothing()
    {
        var r = RunRemnant(20, 0, 0, 0, true, Attack(9));
        Assert.Equal(0, r.MaxLoss);
    }

    [Fact]
    public void BeatingRemnant_WithTungstenRod_FollowsRelicOrder()
    {
        // 25 then 25. Rod first: 24 capped to 20, then 0. Remnant first: 20 - 1 = 19, then 1 left - 1 = 0.
        Assert.Equal(20, RunRemnant(0, 0, 0, 1, true, Attack(25, 2)).MinLoss);
        Assert.Equal(19, RunRemnant(0, 0, 0, 1, false, Attack(25, 2)).MinLoss);
    }

    [Fact]
    public void BeatingRemnant_BufferNotSpentOnceTheTurnIsCapped()
    {
        // The first hit uses the whole 20, so the second costs nothing and Buffer is kept... for the first hit:
        // Buffer cancels the 20 hit, leaving the budget for the second.
        var r = RunRemnant(0, 0, 1, 0, true, Attack(25), Attack(7));
        Assert.Equal(7, r.MinLoss);
    }

    [Fact]
    public void Poison_TicksAfterEnemyAttacks_SoBufferTakesTheAttack()
    {
        // Without Remnant too: Poison comes after the enemy turn, so Buffer is spent on the attack, not Poison.
        var r = DamageForecast.Compute(new ForecastInputs(40, 50, 0, 0, false,
            new List<ForecastItem> { Poison(3), Attack(12) }, 0, -1, 1));
        Assert.Equal(3, r.MinLoss);
    }
}
