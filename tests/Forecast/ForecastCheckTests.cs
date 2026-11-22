using System.Linq;
using Xunit;
using STS2_DamageCharts.Forecast;

public class ForecastCheckTests
{
    private static ForecastItem Attack(int hits, int per, bool conditional = false, string label = "Cultist")
        => new(label, ForecastKind.Attack, hits, per, blockable: true, conditional: conditional);

    private static (ForecastResult R, ForecastInputs In) Forecast(int block, bool hidden = false, int buffer = 0,
        params ForecastItem[] items)
    {
        var inp = new ForecastInputs(50, 80, block, 0, hidden, items, buffer: buffer);
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
        Assert.Null(check.RoundStarted(3, 40));                 // same round: still pending
        var miss = check.RoundStarted(4, 34);                   // 14 taken since the freeze: Buffer didn't trigger
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
        Assert.Null(check.RoundStarted(2, 7));
        Assert.Empty(check.Misses);
    }

    [Fact]
    public void CombatEndingFirstSkipsTheRound()
    {
        var check = new ForecastCheck();
        var (r, inp) = Forecast(0, false, 0, Attack(1, 30));
        check.Freeze(5, r, inp, 0);
        check.CombatEnded();                       // died (or won) during the enemy turn
        Assert.Null(check.RoundStarted(6, 0));
        Assert.Empty(check.Misses);
    }

    [Fact]
    public void NoFreezeMeansNothingToCheck()
    {
        var check = new ForecastCheck();
        Assert.Null(check.RoundStarted(2, 99));
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
        Assert.Null(check.RoundStarted(3, 15));    // 5 since the second freeze: in range of the second forecast
    }

    [Fact]
    public void BeginFightClearsMissesAndThePendingCheck()
    {
        var check = new ForecastCheck();
        var (r, inp) = Forecast(0, false, 0, Attack(1, 10));
        check.Freeze(1, r, inp, 0);
        check.RoundStarted(2, 0);
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
            Assert.NotNull(check.RoundStarted(round + 1, 0));   // nothing taken: below the minimum of 30
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
}
