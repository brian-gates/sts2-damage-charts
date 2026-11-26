namespace STS2_DamageCharts.Tracking;

// Pure (unit-tested): an enemy that kills itself on its own move is not damage dealt by a player.
//
// Some monsters end by killing themselves: the Waterfall Giant's TriggerAboutToBlowState sets its HP to 999,999,999
// and its ExplodeMove later calls CreatureCmd.Kill on itself; a Gas Bomb's ExplodeMove does the same. That HP loss
// carries no Damage tag and no fresh power/relic stamp, so the mod used to credit it to the local player as "Status".
//
// Two decisions:
//   - IsSelfKill, when CreatureCmd.Kill runs: the killed creature is the monster whose move is being performed
//     (MonsterModel.PerformMove, this enemy turn and round), the kill is not by a card, no player's power/relic is
//     acting right now, and it still has HP for the Kill to take (a Kill after a hit that already took it to 0
//     changes no HP);
//   - Credit, when its HP drops: a tagged hit (a Damage call, or Doom's pre-tag) is credited as before; an untagged
//     HP loss on a creature marked as a self-kill (within ttl frames) is not credited to anyone.
internal static class SelfKill
{
    public static bool IsSelfKill<T>(T killed, T? mover, int moverRound, int round, bool enemyTurn, bool byCard,
        bool playerSourceFresh, int hpBefore) where T : class =>
        enemyTurn && mover != null && ReferenceEquals(killed, mover) && moverRound == round
        && !byCard && !playerSourceFresh && hpBefore > 0;

    public static bool Credit(bool tagged, long? selfKillFrame, long frame, long ttl) =>
        tagged || selfKillFrame is not { } marked || frame - marked > ttl;
}
