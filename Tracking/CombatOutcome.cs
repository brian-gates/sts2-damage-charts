namespace STS2_DamageCharts.Tracking;

// Pure (unit-tested): did a combat that just ended reach a real conclusion (a win or a loss), as opposed to being
// abandoned (quit to menu mid-fight, which the game replays on continue, so it must not be written or summarized)?
//
// Decided once, at the end transition, from what is true then, not only from what a frame happened to sample:
//   - sampledEnding: a tick saw the game's own ending flags (CombatManager.IsOverOrEnding / IsEnding) or the
//     debounced all-enemies-dead state while the combat was still in progress. A kill inside a card play leaves
//     frames for that; a kill that resolves without an await (an orb's end-of-turn passive, a console kill) can
//     end the combat within one frame, so no tick ever sees it;
//   - localPlayerDead: a loss (or, in co-op, this player's death);
//   - enemies / enemiesGone: every enemy seen in this fight, and how many are dead or escaped. All gone = a win
//     (or an escape), whatever dealt the last blow. An abandon leaves enemies alive.
internal static class CombatOutcome
{
    public static bool Resolved(bool sampledEnding, bool localPlayerDead, int enemies, int enemiesGone) =>
        sampledEnding || localPlayerDead || (enemies > 0 && enemiesGone >= enemies);
}
