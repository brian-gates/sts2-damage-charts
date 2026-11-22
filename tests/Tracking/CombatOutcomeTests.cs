using Xunit;
using STS2_DamageCharts.Tracking;

public class CombatOutcomeTests
{
    [Fact]
    public void CardKill_TheTickSawTheEndingFlags() =>
        Assert.True(CombatOutcome.Resolved(sampledEnding: true, localPlayerDead: false, enemies: 2, enemiesGone: 2));

    [Fact]
    public void EndOfTurnOrbKill_EndsWithinAFrameButEveryEnemyIsDead() =>
        // The 1.6.1 bug: Lightning's passive killed the last enemy and the combat ended before any tick saw
        // IsEnding, so the fight was treated as abandoned (no summary, no stats file).
        Assert.True(CombatOutcome.Resolved(sampledEnding: false, localPlayerDead: false, enemies: 4, enemiesGone: 4));

    [Fact]
    public void PoisonAtTurnStart_OrThornsReflect_OrAConsoleKill() =>
        Assert.True(CombatOutcome.Resolved(false, false, enemies: 1, enemiesGone: 1));

    [Fact]
    public void AnEscapedEnemyCountsAsGone() =>
        // One killed, one escaped (counted in enemiesGone by the caller from CombatState.EscapedCreatures).
        Assert.True(CombatOutcome.Resolved(false, false, enemies: 2, enemiesGone: 2));

    [Fact]
    public void PlayerDeath_IsALoss() =>
        Assert.True(CombatOutcome.Resolved(false, localPlayerDead: true, enemies: 3, enemiesGone: 1));

    [Fact]
    public void Abandon_QuitToMenuMidFight_StaysUnresolved() =>
        Assert.False(CombatOutcome.Resolved(false, false, enemies: 3, enemiesGone: 1));

    [Fact]
    public void NoEnemiesSeen_IsNotAWin() =>
        Assert.False(CombatOutcome.Resolved(false, false, enemies: 0, enemiesGone: 0));
}
