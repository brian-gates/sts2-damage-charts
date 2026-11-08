using STS2_DamageCharts;
using Xunit;
using O = STS2_DamageCharts.IntentRules.Outcome;

public class IntentRulesTests
{
    private static IntentThreat C(string? intentType, params string[] chain) => IntentRules.Classify(chain, intentType);

    [Fact]
    public void Classify_ByTypeChainAndIntentType()
    {
        Assert.Equal(IntentThreat.Attack, C(null, "SingleAttackIntent", "AttackIntent", "AbstractIntent"));
        Assert.Equal(IntentThreat.Attack, C(null, "MultiAttackIntent", "AbstractIntent"));
        Assert.Equal(IntentThreat.Attack, C("Attack", "SomeIntent"));
        // The Waterfall Giant's Explode: neither the type nor IntentType says "Attack".
        Assert.Equal(IntentThreat.DeathBlow, C("DeathBlow", "DeathBlowIntent", "AbstractIntent"));
        // Even if it derives from an attack intent it is a death blow (it counts at 0 HP too).
        Assert.Equal(IntentThreat.DeathBlow, C(null, "DeathBlowIntent", "AttackIntent", "AbstractIntent"));
        Assert.Equal(IntentThreat.Unknown, C(null, "HiddenIntent", "AbstractIntent"));
        Assert.Equal(IntentThreat.Unknown, C(null, "UnknownIntent", "AbstractIntent"));
        Assert.Equal(IntentThreat.None, C("Buff", "BuffIntent", "AbstractIntent"));
        Assert.Equal(IntentThreat.None, C(null, "DefendIntent", "AbstractIntent"));
    }

    [Theory]
    // Alive enemies: attacks with a number count; a modified 0 is a real "no threat"; hidden flags.
    [InlineData(IntentThreat.Attack, true, 12, O.Add)]
    [InlineData(IntentThreat.Attack, true, 0, O.Skip)]
    [InlineData(IntentThreat.Unknown, true, 0, O.Hidden)]
    [InlineData(IntentThreat.None, true, 0, O.Skip)]
    // Death blow: counts whether or not the enemy reads as alive, and is hidden when unreadable.
    [InlineData(IntentThreat.DeathBlow, true, 27, O.Add)]
    [InlineData(IntentThreat.DeathBlow, false, 27, O.Add)]
    [InlineData(IntentThreat.DeathBlow, true, 0, O.Hidden)]
    [InlineData(IntentThreat.DeathBlow, false, 0, O.Hidden)]
    // A creature kept in combat at 0 HP (e.g. waiting to revive): its leftover intents are not moves.
    [InlineData(IntentThreat.Attack, false, 12, O.Skip)]
    [InlineData(IntentThreat.Unknown, false, 0, O.Skip)]
    public void Resolve(object threat, bool alive, int perHit, object expected)
    {
        Assert.Equal((O)expected, IntentRules.Resolve((IntentThreat)threat, alive, perHit));
    }
}
