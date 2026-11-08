using STS2_DamageCharts;
using Xunit;

public class SelfHarmTextTests
{
    [Theory]
    [InlineData("Lose 6 HP. Gain 2 Energy.", 6)]
    [InlineData("Take 3 damage.", 3)]
    [InlineData("Exhaust. Lose 4 HP.", 4)]
    [InlineData("Retain. Take 5 damage. Draw 2 cards.", 5)]
    [InlineData("At the end of your turn, if this is in your hand, lose 2 HP.", 2)]
    [InlineData("Deal 10 damage. You lose 3 HP.", 3)]
    [InlineData("Deal 12 damage to ALL enemies. Lose 2 HP.", 2)]
    public void PlayerIsSubject_IsSelfHarm_WithAmountFromSelfClause(string desc, int amount)
    {
        Assert.True(SelfHarmText.TryMatch(desc, out int n));
        Assert.Equal(amount, n);
    }

    [Theory]
    [InlineData("Retain. Whenever you apply a debuff to an enemy, they take 9 damage.")] // Sleight of Flesh
    [InlineData("Deal 8 damage. Enemies lose 2 Strength.")]
    [InlineData("All enemies take 5 damage.")]
    [InlineData("it takes 4 damage")]
    [InlineData("Apply 2 Vulnerable. The target takes 3 damage.")]
    [InlineData("A random enemy takes 7 damage.")]
    [InlineData("Whenever you lose HP, gain 2 Strength.")]
    [InlineData("If you take damage this turn, draw 1 card.")]
    [InlineData("Deal 6 damage.")]
    [InlineData("")]
    public void SomeoneElseOrConditionOnly_IsNotSelfHarm(string desc)
    {
        Assert.False(SelfHarmText.IsSelfHarm(desc));
    }

    [Fact]
    public void SelfClauseWithoutNumber_MatchesWithZeroAmount()
    {
        Assert.True(SelfHarmText.TryMatch("Lose HP equal to the number of cards in your hand.", out int n));
        Assert.Equal(0, n);
    }

    [Fact]
    public void AmountComesFromSelfClause_NotAnEarlierEnemyClause()
    {
        Assert.True(SelfHarmText.TryMatch("Enemies take 9 damage. Lose 2 HP.", out int n));
        Assert.Equal(2, n);
    }
}
