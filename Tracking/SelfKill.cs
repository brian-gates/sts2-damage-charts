namespace STS2_DamageCharts.Tracking;

// What tagged an enemy's HP loss before it happened: nothing, a CreatureCmd.Damage call (DamagePrefix, including a
// DoT's tag), or Doom's pre-tag (DoomKillPrefix, which runs before DoomKill calls CreatureCmd.Kill).
internal enum HpTag { None, Damage, DoomKill }

// Pure (unit-tested): an enemy that kills itself on its own move is not damage dealt by a player.
//
// Some monsters end by killing themselves: the Waterfall Giant's TriggerAboutToBlowState sets its HP to 999,999,999
// and its ExplodeMove later calls CreatureCmd.Kill on itself; a Gas Bomb's ExplodeMove does the same. That HP loss
// carries no Damage tag and no fresh power/relic stamp, so the mod used to credit it to the local player as "Status".
//
// Two decisions:
//   - IsSelfKill, in a prefix on the innermost CreatureCmd.Kill(IReadOnlyCollection<Creature>, CardModel, bool):
//     the killed monster is performing its own move (the game's MonsterModel.IsPerformingMove), the kill is not by a
//     card, no player's power/relic is acting right now, and it still has HP for the Kill to take (a Kill after a hit
//     that already took it to 0 changes no HP). The Kill's HP loss (LoseHpInternal) follows synchronously, so the
//     mark is only valid in the frame it was made;
//   - Credit, when its HP drops: a self-kill marked this frame beats every Damage tag (a Kill never goes through
//     CreatureCmd.Damage, so such a tag is a leftover from a hit that changed no HP, e.g. a fully blocked one). Only
//     Doom's pre-tag still credits: a Doom kill is a player's kill, not the monster's move. Without a mark, the
//     existing attribution (tag, fresh stamp, or the "Status" fallback) is unchanged.
internal static class SelfKill
{
    public static bool IsSelfKill(bool performingOwnMove, bool byCard, bool playerSourceFresh, int hpBefore) =>
        performingOwnMove && !byCard && !playerSourceFresh && hpBefore > 0;

    // A self-kill mark applies only to the HP change in the frame it was made.
    public static bool MarkedNow(long? markFrame, long frame) => markFrame == frame;

    public static bool Credit(HpTag tag, bool selfKillNow) => tag == HpTag.DoomKill || !selfKillNow;
}
