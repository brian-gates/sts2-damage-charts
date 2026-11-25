using STS2_DamageCharts.Forecast;
using STS2_DamageCharts.Live;
using Xunit;

namespace STS2_DamageCharts.Tests.Live;

public class LiveIntentKindTests
{
    [Theory]
    [InlineData("Attack", "SingleAttackIntent", "Attack", "attack")]
    [InlineData("DeathBlow", "DeathBlowIntent", "DeathBlow", "attack")]
    [InlineData("Unknown", "HiddenIntent", "Hidden", "unknown")]
    [InlineData("None", "BuffIntent", "Buff", "buff")]
    [InlineData("None", "DebuffIntent", "Debuff", "debuff")]
    [InlineData("None", "CardDebuffIntent", "CardDebuff", "debuff")]
    [InlineData("None", "StatusIntent", "Status", "debuff")]
    [InlineData("None", "DefendIntent", "Defend", "defend")]
    [InlineData("None", "SleepIntent", "Sleep", "unknown")]
    [InlineData("None", "SummonIntent", "Summon", "unknown")]
    [InlineData("None", "", "", "unknown")]
    public void Maps_the_games_intent_to_one_of_the_five_kinds(string threat, string typeName, string intentType, string kind)
        => Assert.Equal(kind, LiveIntentKind.Of(System.Enum.Parse<IntentThreat>(threat), typeName, intentType));

    [Fact]
    public void The_most_informative_intent_wins()
    {
        var order = new[] { "attack", "unknown", "debuff", "buff", "defend" };
        for (int i = 0; i < order.Length; i++)
            for (int j = i + 1; j < order.Length; j++)
                Assert.True(LiveIntentKind.Rank(order[i]) < LiveIntentKind.Rank(order[j]), $"{order[i]} before {order[j]}");
    }
}
