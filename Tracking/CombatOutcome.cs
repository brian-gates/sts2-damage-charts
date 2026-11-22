using System;
using System.Collections.Generic;

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
//   - enemies / enemiesGone: every enemy seen in this fight, and how many are dead or escaped (EnemiesGone). All
//     gone = a win (or an escape), whatever dealt the last blow. A quit mid-fight normally leaves enemies alive;
//     the stats file has a second guard, since it is written only if the run is unchanged at combat end.
internal static class CombatOutcome
{
    public static bool Resolved(bool sampledEnding, bool localPlayerDead, int enemies, int enemiesGone) =>
        sampledEnding || localPlayerDead || (enemies > 0 && enemiesGone >= enemies);

    // How many of `enemies` are gone: dead, or escaped as sampled during the fight, or escaped as the combat
    // state lists them at the end (an escape can end the fight in the same frame, e.g. the Battleworn Dummy's
    // time limit, before any tick samples it).
    public static (int Enemies, int Gone) EnemiesGone<T>(IEnumerable<T> enemies, Func<T, bool> isDead,
        ICollection<T> escapedSampled, IEnumerable<T>? escapedAtEnd) where T : class
    {
        var atEnd = escapedAtEnd != null ? new HashSet<T>(escapedAtEnd) : null;
        int n = 0, gone = 0;
        foreach (var e in enemies)
        {
            if (e == null) continue;
            n++;
            if (isDead(e) || escapedSampled.Contains(e) || (atEnd != null && atEnd.Contains(e))) gone++;
        }
        return (n, gone);
    }
}
