using System.Collections.Generic;
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

    private sealed class Enemy { public bool Dead; }

    [Fact]
    public void LastEnemyEscapesInTheEndFrame_IsCountedFromTheEndState()
    {
        // Battleworn Dummy: its time limit makes it flee and the fight ends in the same frame, so no tick sampled
        // the escape; the combat state's EscapedCreatures at the end still lists it.
        var dummy = new Enemy();
        var killed = new Enemy { Dead = true };
        var sampled = new HashSet<Enemy>();
        var (n, gone) = CombatOutcome.EnemiesGone(new[] { killed, dummy }, e => e.Dead, sampled, new[] { dummy });
        Assert.Equal((2, 2), (n, gone));
        Assert.True(CombatOutcome.Resolved(false, false, n, gone));
        // Without the end-state list (e.g. after a quit's Reset), the fled dummy reads as alive: unresolved.
        var (n2, gone2) = CombatOutcome.EnemiesGone(new[] { killed, dummy }, e => e.Dead, sampled, null);
        Assert.False(CombatOutcome.Resolved(false, false, n2, gone2));
    }

    [Fact]
    public void EnemiesGone_CountsDeadAndSampledEscapes()
    {
        var a = new Enemy { Dead = true }; var b = new Enemy(); var c = new Enemy();
        var (n, gone) = CombatOutcome.EnemiesGone(new[] { a, b, c, null! }, e => e.Dead, new HashSet<Enemy> { b }, null);
        Assert.Equal((3, 2), (n, gone));
    }

    [Fact]
    public void NoEnemiesSeen_IsNotAWin() =>
        Assert.False(CombatOutcome.Resolved(false, false, enemies: 0, enemiesGone: 0));
}
