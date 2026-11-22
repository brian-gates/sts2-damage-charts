using System.Linq;
using Xunit;
using STS2_DamageCharts.Forecast;

public class ForecastCheckTests
{
    private static ForecastItem Attack(int hits, int per, bool conditional = false, string label = "Cultist")
        => new(label, ForecastKind.Attack, hits, per, blockable: true, conditional: conditional);

    private static (ForecastResult R, ForecastInputs In) Forecast(int block, bool hidden = false, int buffer = 0,
        params ForecastItem[] items) => ForecastAt(50, block, hidden, buffer, items);

    private static (ForecastResult R, ForecastInputs In) ForecastAt(int hp, int block, bool hidden = false, int buffer = 0,
        params ForecastItem[] items)
    {
        var inp = new ForecastInputs(hp, 80, block, 0, hidden, items, buffer: buffer);
        return (DamageForecast.Compute(inp), inp);
    }

    [Theory]
    [InlineData(10, false, false)]  // exactly the minimum
    [InlineData(9, false, true)]    // below the minimum
    [InlineData(16, false, false)]  // at the maximum (conditional included)
    [InlineData(17, false, true)]   // above the maximum, nothing hidden
    [InlineData(17, true, false)]   // above the maximum, but an intent was hidden
    [InlineData(3, true, true)]     // below the minimum is a miss even with a hidden intent
    public void MissRules(int actual, bool hidden, bool miss)
    {
        var (r, inp) = Forecast(0, hidden, 0, Attack(1, 10), Attack(1, 6, conditional: true, label: "Spiker"));
        Assert.Equal(10, r.MinLoss);
        Assert.Equal(16, r.MaxLoss);
        Assert.Equal(miss, ForecastCheck.IsMiss(r, actual));
        Assert.Equal(miss, ForecastCheck.Evaluate(2, r, inp, actual) != null);
    }

    [Fact]
    public void EvaluatesAtTheNextRoundWithTakenSinceTheFreeze()
    {
        var check = new ForecastCheck();
        check.BeginFight();
        var (r, inp) = Forecast(4, false, 1, Attack(2, 9));     // 2x9 vs 4 block: 5 + 9, Buffer cancels the first
        Assert.Equal(9, r.MinLoss);
        check.Freeze(3, r, inp, takenSoFar: 20);
        Assert.True(check.HasPending);
        Assert.Null(check.PlayStarted(3, 40));                 // same round: still pending
        var miss = check.PlayStarted(4, 34);                   // 14 taken since the freeze: Buffer didn't trigger
        Assert.NotNull(miss);
        Assert.False(check.HasPending);
        Assert.Equal(3, miss!.Round);
        Assert.Equal(9, miss.Min);
        Assert.Equal(9, miss.Max);
        Assert.Equal(14, miss.Actual);
        Assert.Equal(4, miss.Block);
        Assert.Equal(1, miss.Buffer);
        Assert.False(miss.Intangible);
        Assert.Equal(new ForecastMissItem("attack", "Cultist", 2, 9, false), Assert.Single(miss.Items));
        Assert.Same(miss, Assert.Single(check.Misses));
    }

    [Fact]
    public void AHitInRangeIsNotRecorded()
    {
        var check = new ForecastCheck();
        var (r, inp) = Forecast(0, false, 0, Attack(1, 7));
        check.Freeze(1, r, inp, 0);
        Assert.Null(check.PlayStarted(2, 7));
        Assert.Empty(check.Misses);
    }

    [Fact]
    public void CombatEndingFirstSkipsTheRound()
    {
        var check = new ForecastCheck();
        var (r, inp) = Forecast(0, false, 0, Attack(1, 30));
        check.Freeze(5, r, inp, 0);
        check.CombatEnded();                       // died (or won) during the enemy turn
        Assert.Null(check.PlayStarted(6, 0));
        Assert.Empty(check.Misses);
    }

    [Fact]
    public void NoFreezeMeansNothingToCheck()
    {
        var check = new ForecastCheck();
        Assert.Null(check.PlayStarted(2, 99));
        Assert.Empty(check.Misses);
    }

    [Fact]
    public void ALaterFreezeInTheSameRoundReplacesTheFirst()
    {
        var check = new ForecastCheck();
        var (a, ain) = Forecast(0, false, 0, Attack(1, 30));
        var (b, bin) = Forecast(0, false, 0, Attack(1, 5));
        check.Freeze(2, a, ain, 0);
        check.Freeze(2, b, bin, 10);
        Assert.Null(check.PlayStarted(3, 15));    // 5 since the second freeze: in range of the second forecast
    }

    [Fact]
    public void BeginFightClearsMissesAndThePendingCheck()
    {
        var check = new ForecastCheck();
        var (r, inp) = Forecast(0, false, 0, Attack(1, 10));
        check.Freeze(1, r, inp, 0);
        check.PlayStarted(2, 0);
        check.Freeze(2, r, inp, 0);
        check.BeginFight();
        Assert.Empty(check.Misses);
        Assert.False(check.HasPending);
    }

    [Fact]
    public void MissesAndItemsAreCapped()
    {
        var check = new ForecastCheck();
        var items = Enumerable.Range(0, 30).Select(i => Attack(1, 1, label: "E" + i)).ToArray();
        var (r, inp) = Forecast(0, false, 0, items);
        for (int round = 1; round <= 25; round++)
        {
            check.Freeze(round, r, inp, 0);
            Assert.NotNull(check.PlayStarted(round + 1, 0));   // nothing taken: below the minimum of 30
        }
        Assert.Equal(ForecastCheck.MaxMisses, check.Misses.Count);
        Assert.Equal(ForecastCheck.MaxItems, check.Misses[0].Items.Count);
        Assert.Equal("E0", check.Misses[0].Items[0].Label);
    }

    [Fact]
    public void NegativeActualCountsAsZero()
    {
        // Taken can't go down, but a reset between freeze and round start must not write a negative amount.
        var (r, inp) = Forecast(0, false, 0, Attack(1, 4));
        var miss = ForecastCheck.Evaluate(1, r, inp, -3);
        Assert.Equal(0, miss!.Actual);
    }

    [Fact]
    public void KindNamesAreShortStrings()
    {
        Assert.Equal("attack", ForecastCheck.KindName(ForecastKind.Attack));
        Assert.Equal("dot", ForecastCheck.KindName(ForecastKind.Dot));
        Assert.Equal("self", ForecastCheck.KindName(ForecastKind.SelfCard));
        Assert.Equal("hp_loss", ForecastCheck.KindName(ForecastKind.NonAttackHpLoss));
        Assert.Equal("hidden", ForecastCheck.KindName(ForecastKind.Hidden));
    }

    // ===== Review I3: lethal forecasts =====

    [Fact]
    public void ACorrectLethalForecastIsNotAMiss()
    {
        // 30 forecast at 12 HP: the player can lose at most 12 (co-op partner fights on, or a revive).
        var (r, inp) = ForecastAt(12, 0, false, 0, Attack(1, 30));
        Assert.True(r.IsLethal);
        Assert.False(ForecastCheck.IsMiss(r, 12));
        Assert.True(ForecastCheck.IsMiss(r, 11));          // less than the HP they had is still a miss
        Assert.False(ForecastCheck.IsMiss(r, 40));         // a revive let more HP be lost: never "over"
    }

    // ===== Review I1/I2: when to freeze and when to evaluate (Observe, every combat tick) =====

    private static (ForecastResult, ForecastInputs)? Fresh((ForecastResult R, ForecastInputs In) f) => (f.R, f.In);

    [Fact]
    public void Observe_FreezesWhenPlanningEndsAndChecksAtTheNextPlayPhase()
    {
        var check = new ForecastCheck();
        check.BeginFight();
        long taken = 10;
        var f = Forecast(0, false, 0, Attack(1, 8), new ForecastItem("Poison", ForecastKind.Dot, 1, 5, blockable: false, conditional: false));
        Assert.Equal(13, f.R.MinLoss);
        Assert.Null(check.Observe(1, true, true, () => taken, Fresh(f)));      // round 1 play
        Assert.Null(check.Observe(1, false, false, () => taken, Fresh(f)));    // end turn: freeze at 10
        Assert.True(check.HasPending);
        taken += 8;                                                            // enemy hits for 8
        Assert.Null(check.Observe(2, true, false, () => taken, null));         // round 2 starts (turn-start phase)
        taken += 5;                                                            // Poison ticks at side turn start
        var miss = check.Observe(2, true, true, () => taken, null);            // Play phase: evaluate
        Assert.Null(miss);                                                     // 13 = forecast: not a miss
        Assert.False(check.HasPending);
        Assert.Empty(check.Misses);
    }

    [Fact]
    public void Observe_RecordsAMissAtThePlayPhase()
    {
        var check = new ForecastCheck();
        check.BeginFight();
        long taken = 0;
        var f = Forecast(0, false, 0, Attack(1, 0));                           // forecast 0-0
        check.Observe(3, true, true, () => taken, Fresh(f));
        check.Observe(3, false, false, () => taken, Fresh(f));
        taken = 7;                                                             // unforecast damage
        var miss = check.Observe(4, true, true, () => taken, null);
        Assert.Equal(7, miss!.Actual);
        Assert.Equal(3, miss.Round);
    }

    [Fact]
    public void Observe_SkipsTheRoundWhenTheForecastWasNotFreshAtTheFreeze()
    {
        // The breakdown was open when the turn ended: no fresh forecast, so the round is skipped, never checked
        // against a late baseline or an older round's forecast.
        var check = new ForecastCheck();
        check.BeginFight();
        long taken = 0;
        var f = Forecast(0, false, 0, Attack(1, 20));
        check.Observe(1, true, true, () => taken, Fresh(f));                   // seen while planning
        check.Observe(1, true, true, () => taken, null);                       // then hidden
        check.Observe(1, false, false, () => taken, null);                     // turn ends while hidden
        Assert.False(check.HasPending);
        Assert.Null(check.Observe(2, true, true, () => 0, null));
        Assert.Empty(check.Misses);
    }

    [Fact]
    public void Observe_CombatEndingDropsThePendingCheck()
    {
        var check = new ForecastCheck();
        check.BeginFight();
        long taken = 0;
        var f = Forecast(0, false, 0, Attack(1, 5));
        check.Observe(1, true, true, () => taken, Fresh(f));
        check.Observe(1, false, false, () => taken, Fresh(f));                 // round 1 frozen
        check.CombatEnded();
        Assert.Null(check.Observe(2, true, true, () => 99, null));
        Assert.Empty(check.Misses);
    }

    [Fact]
    public void Observe_TakenIsOnlyReadOnEdges()
    {
        var check = new ForecastCheck();
        check.BeginFight();
        int reads = 0;
        long Taken() { reads++; return 0; }
        var f = Forecast(0, false, 0, Attack(1, 5));
        for (int i = 0; i < 5; i++) check.Observe(1, true, true, Taken, Fresh(f));
        Assert.Equal(0, reads);
        check.Observe(1, false, false, Taken, Fresh(f));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void Observe_CoopLocalEndingEarlyDoesNotEvaluateOrFreeze()
    {
        // The local player ends their turn while a teammate still plays: planning continues, nothing happens.
        var check = new ForecastCheck();
        check.BeginFight();
        long taken = 0;
        var f = Forecast(0, false, 0, Attack(1, 5));
        check.Observe(1, true, true, () => taken, Fresh(f));
        check.Observe(1, true, false, () => taken, Fresh(f));
        Assert.False(check.HasPending);
        check.Observe(1, false, false, () => taken, Fresh(f));
        Assert.True(check.HasPending);
    }
}
