using System;
using System.Collections.Generic;

namespace STS2_DamageCharts.Forecast;

internal enum ForecastKind { Attack, Dot, SelfCard, NonAttackHpLoss, Hidden }

// One contributing source of predicted HP loss. Attacks are Blockable; DoTs / non-attack HP loss /
// self-damage cards bypass block. Conditional items (e.g. self-damage-if-played) count toward the
// range MAXIMUM only, never the guaranteed minimum.
internal readonly struct ForecastItem
{
    public readonly string Label;
    public readonly ForecastKind Kind;
    public readonly int Hits;
    public readonly int DamagePerHit;
    public readonly bool Blockable;
    public readonly bool Conditional;
    // Lands at the start of the player's next turn, after the enemy turn (Poison: PoisonPower.AfterSideTurnStart).
    public readonly bool NextTurnStart;

    public ForecastItem(string label, ForecastKind kind, int hits, int damagePerHit, bool blockable, bool conditional,
                        bool nextTurnStart = false)
    {
        NextTurnStart = nextTurnStart;
        Label = label;
        Kind = kind;
        Hits = hits;
        DamagePerHit = damagePerHit;
        Blockable = blockable;
        Conditional = conditional;
    }

    public int Total => Hits * DamagePerHit;
}

internal readonly struct ForecastInputs
{
    public readonly int CurrentHp;
    public readonly int MaxHp;
    public readonly int Block;
    public readonly int OstyAbsorb;   // Osty's (block + HP) — a damage sink in front of the player; 0 if none/dead
    public readonly bool HasHidden;
    public readonly IReadOnlyList<ForecastItem> Items;
    public readonly int EndTurnBlock; // block gained at the end of your turn (Plating, Orichalcum, ...), before enemies act
    public readonly int OstyHp;       // Osty's HP part of OstyAbsorb (the rest is its block)
    public readonly int Buffer;       // Buffer stacks: each cancels one HP loss (after block and Osty)
    public readonly bool Intangible;  // every hit on the player deals and costs at most 1
    // Intangible stacks. One is spent at the end of the enemy turn (IntangiblePower.AfterSideTurnEnd), so Poison at the
    // start of the next turn is only capped when there were at least 2.
    public readonly int IntangibleStacks;
    // Wuppy (the Wuppy relic's pet): while alive, every attack on the player is aimed at it instead, before
    // block. 0 HP = no Wuppy (its block alone doesn't count).
    public readonly int WuppyHp;
    public readonly int WuppyBlock;
    // HP loss reduction (Tungsten Rod): every hit that still costs the player HP after block and Osty costs this
    // much less, never below 0 (TungstenRod.ModifyHpLostAfterOsty).
    public readonly int HpLossReduction;
    // Beating Remnant: at most TurnHpLossLimit HP lost per turn (0 = no limit). The count, TurnHpLossSoFar, is HP
    // actually lost since the player's turn started and resets when their next turn starts. Both it and the Rod are
    // relics (ModifyHpLostAfterOsty), so they apply in the player's relic order: ReductionBeforeLimit.
    public readonly int TurnHpLossLimit;
    public readonly int TurnHpLossSoFar;
    public readonly bool ReductionBeforeLimit;
    // Healing at the end of the player's turn, before the enemy turn (Regen: RegenPower.BeforeSideTurnEndEarly).
    public readonly int EndTurnHeal;
    // What prevents the player's death this turn, if anything: Fairy in a Bottle (ShouldDie), else an unused
    // Lizard Tail (ShouldDieLate). Null when nothing does.
    public readonly string? Revive;

    // ostyHp < 0: the HP/block split is unknown, so all of OstyAbsorb is treated as HP.
    public ForecastInputs(int currentHp, int maxHp, int block, int ostyAbsorb, bool hasHidden, IReadOnlyList<ForecastItem> items,
                          int endTurnBlock = 0, int ostyHp = -1, int buffer = 0, bool intangible = false,
                          int wuppyHp = 0, int wuppyBlock = 0, int hpLossReduction = 0,
                          int turnHpLossLimit = 0, int turnHpLossSoFar = 0, bool reductionBeforeLimit = true,
                          int endTurnHeal = 0, string? revive = null, int intangibleStacks = -1)
    {
        IntangibleStacks = intangibleStacks >= 0 ? intangibleStacks : (intangible ? 1 : 0);
        EndTurnHeal = Math.Max(0, endTurnHeal);
        Revive = string.IsNullOrEmpty(revive) ? null : revive;
        TurnHpLossLimit = Math.Max(0, turnHpLossLimit);
        TurnHpLossSoFar = Math.Max(0, turnHpLossSoFar);
        ReductionBeforeLimit = reductionBeforeLimit;
        HpLossReduction = Math.Max(0, hpLossReduction);
        WuppyHp = Math.Max(0, wuppyHp);
        WuppyBlock = WuppyHp > 0 ? Math.Max(0, wuppyBlock) : 0;
        CurrentHp = currentHp;
        MaxHp = maxHp;
        Block = block;
        OstyAbsorb = ostyAbsorb;
        HasHidden = hasHidden;
        Items = items ?? Array.Empty<ForecastItem>();
        EndTurnBlock = endTurnBlock;
        int absorb = Math.Max(0, ostyAbsorb);
        OstyHp = ostyHp < 0 ? absorb : Math.Min(ostyHp, absorb);
        Buffer = Math.Max(0, buffer);
        Intangible = intangible;
    }
}

internal readonly struct ForecastResult
{
    public readonly int Incoming;   // guaranteed gross threat (attacks + guaranteed unblockable), pre-block
    public readonly int Mitigated;  // block absorbed vs guaranteed attacks
    public readonly int MinLoss;    // guaranteed HP loss after block + Osty
    public readonly int MaxLoss;    // MinLoss + conditional contributions
    public readonly int Block;
    public readonly int OstyAbsorb; // Osty pool in front of the player (block + HP), 0 if none
    public readonly int CurrentHp;
    public readonly int MaxHp;
    public readonly bool HasHidden;
    public readonly IReadOnlyList<ForecastItem> Items;
    // Player block left over after the worst case of every blockable attack (Osty absorbs first only when
    // the block alone can't cover it); 0 when the block is all needed.
    public readonly int Overblock;
    // Worst-case HP Osty loses this enemy turn (every blockable attack, conditional ones included, after the
    // player's block and then Osty's own block), and whether that kills it. 0 / false without Osty.
    public readonly int OstyHp;
    public readonly int OstyHpLoss;
    public bool OstyDies => OstyHp > 0 && OstyHpLoss >= OstyHp;
    // Wuppy's pool (block + HP; 0 without a living Wuppy), and its worst-case HP loss this enemy turn.
    public readonly int WuppyAbsorb;
    public readonly int WuppyHp;
    public readonly int WuppyHpLoss;
    public bool WuppyDies => WuppyHp > 0 && WuppyHpLoss >= WuppyHp;
    // HP healed before the enemy turn (Regen, up to max HP), and what revives the player if the turn kills them.
    public readonly int Heal;
    public readonly string? Revive;
    // HP the losses come out of, and the worst case left afterwards (negative when lethal: how far short).
    public int HpBeforeLoss => CurrentHp + Heal;
    public int HpLeft => HpBeforeLoss - MaxLoss;

    public ForecastResult(int incoming, int mitigated, int minLoss, int maxLoss,
        int block, int ostyAbsorb, int currentHp, int maxHp, bool hasHidden, IReadOnlyList<ForecastItem> items,
        int overblock = 0, int ostyHp = 0, int ostyHpLoss = 0, int wuppyHp = 0, int wuppyBlock = 0, int wuppyHpLoss = 0,
        int heal = 0, string? revive = null)
    {
        Heal = Math.Max(0, heal);
        Revive = revive;
        WuppyHp = Math.Max(0, wuppyHp);
        WuppyAbsorb = WuppyHp > 0 ? WuppyHp + Math.Max(0, wuppyBlock) : 0;
        WuppyHpLoss = Math.Clamp(wuppyHpLoss, 0, WuppyHp);
        Overblock = Math.Max(0, overblock);
        OstyHp = Math.Max(0, ostyHp);
        OstyHpLoss = Math.Clamp(ostyHpLoss, 0, OstyHp);
        Incoming = incoming;
        Mitigated = mitigated;
        MinLoss = minLoss;
        MaxLoss = maxLoss;
        Block = block;
        OstyAbsorb = ostyAbsorb;
        CurrentHp = currentHp;
        MaxHp = maxHp;
        HasHidden = hasHidden;
        Items = items ?? Array.Empty<ForecastItem>();
    }

    // Safe only when nothing can cost HP AND nothing is hidden (an unknown intent might still hurt).
    public bool IsSafe => MaxLoss <= 0 && !HasHidden;
    // Lethal: the worst case reaches 0 HP. Revived: it does, but something keeps the player alive.
    public bool IsLethal => CurrentHp > 0 && MaxLoss >= HpBeforeLoss;
    public bool Revived => IsLethal && Revive != null;
}

internal static class DamageForecast
{
    public static ForecastResult Compute(ForecastInputs inp)
    {
        // Hits resolve one at a time, in this order:
        //   1. self damage (except Poison, see 3) (the player's own powers/cards, e.g. Disintegration, Infection, "lose N HP" DoTs):
        //      during or at the end of the player's turn, before enemies act. "Take N damage" spends the
        //      player's block only, never Osty; "lose N HP" bypasses block.
        //   2. enemy items, in intent order: while Wuppy lives, each attack is aimed at Wuppy instead (before
        //      block: Wuppy's block, then its HP; the overflow of the hit that kills it is spent). Otherwise
        //      attacks (incl. death blows) hit player block, then Osty (block, then HP), then the player;
        //      unblockable ones go straight to HP.
        //   3. Poison, at the start of the player's next turn (after Beating Remnant's count resets).
        // Block and Osty are pools, so the order only matters for Buffer (BufferPower.ModifyHpLostAfterOstyLate):
        // each stack cancels the next hit that would still cost HP after block and Osty.
        // Intangible caps every hit on the player at 1 (IntangiblePower.ModifyDamageCap before block, and
        // ModifyHpLostAfterOsty for "lose HP"). Enemy intents already show the capped number (the game's
        // preview runs the cap); self damage and HP loss items don't, so the cap is applied here to every hit.
        // Tungsten Rod takes 1 off each hit's HP loss after block and Osty (ModifyHpLostAfterOsty). In combat the
        // game asks each creature's powers before its relics, so Intangible's cap of 1 comes first (and the Rod
        // makes it 0); Buffer (ModifyHpLostAfterOstyLate) comes after, and isn't spent on a hit already at 0.
        // End-of-turn block lands before the enemy turn, so it counts like block already on the player.
        int block = Math.Max(0, inp.Block) + Math.Max(0, inp.EndTurnBlock);
        int osty = Math.Max(0, inp.OstyAbsorb);
        int ostyHp = Math.Min(Math.Max(0, inp.OstyHp), osty);

        var wuppy = new Wuppy(inp.WuppyHp, inp.WuppyBlock);
        var g = Simulate(inp.Items, block, osty, ostyHp, inp.Buffer, inp.Intangible, inp.IntangibleStacks >= 2, Relics(inp), wuppy, includeConditional: false);
        var w = Simulate(inp.Items, block, osty, ostyHp, inp.Buffer, inp.Intangible, inp.IntangibleStacks >= 2, Relics(inp), wuppy, includeConditional: true);

        // Spare block in the worst case: the player's own block left after self damage and attacks (spare
        // Osty isn't block).
        return new ForecastResult(g.Incoming, g.Mitigated, g.Loss, w.Loss,
            block, osty, inp.CurrentHp, inp.MaxHp, inp.HasHidden, inp.Items, w.BlockLeft, ostyHp, w.OstyHpLoss,
            inp.WuppyHp, inp.WuppyBlock, w.WuppyHpLoss,
            // Regen heals at the start of end-of-turn (before Plating's block and end-of-turn self damage), so it is
            // simply added to the HP the turn's losses come out of.
            Math.Min(inp.EndTurnHeal, Math.Max(0, inp.MaxHp - inp.CurrentHp)), inp.Revive);
    }

    private readonly record struct Wuppy(int Hp, int Block);
    // Tungsten Rod's reduction and Beating Remnant's per-turn limit, in relic order.
    private readonly record struct HpLossRelics(int Reduction, int Limit, int SoFar, bool ReductionFirst);
    private static HpLossRelics Relics(ForecastInputs inp) =>
        new(inp.HpLossReduction, inp.TurnHpLossLimit, inp.TurnHpLossSoFar, inp.ReductionBeforeLimit);
    private readonly record struct Outcome(int Incoming, int Mitigated, int Loss, int BlockLeft, int OstyHpLoss, int WuppyHpLoss);

    private static bool IsSelf(ForecastItem it) => it.Kind == ForecastKind.Dot || it.Kind == ForecastKind.SelfCard;

    private static Outcome Simulate(IReadOnlyList<ForecastItem> items, int block, int osty, int ostyHp, int buffer,
        bool intangible, bool intangibleNextTurn, HpLossRelics relics, Wuppy wuppy, bool includeConditional)
    {
        int ostyBlock = osty - ostyHp, ostyHpLeft = ostyHp;
        int wuppyBlock = wuppy.Block, wuppyHpLeft = wuppy.Hp;
        int incoming = 0, mitigated = 0, loss = 0;
        bool limited = relics.Limit > 0;
        int budget = limited ? Math.Max(0, relics.Limit - relics.SoFar) : int.MaxValue;
        for (int phase = 0; phase < 3; phase++)
        {
            if (phase == 2 && limited) budget = relics.Limit;   // the player's next turn starts: the count resets
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.Kind == ForecastKind.Hidden || it.Total <= 0) continue;
                int itemPhase = it.NextTurnStart ? 2 : IsSelf(it) ? 0 : 1;
                if (itemPhase != phase) continue;
                if (it.Conditional && !includeConditional) continue;
                bool attack = !IsSelf(it) && it.Blockable;
                for (int h = 0; h < it.Hits; h++)
                {
                    bool capped = phase == 2 ? intangibleNextTurn : intangible;
                    int dmg = capped ? Math.Min(1, it.DamagePerHit) : it.DamagePerHit;
                    if (dmg <= 0) continue;
                    if (!it.Conditional) incoming += dmg;
                    if (attack && wuppyHpLeft > 0)
                    {
                        // The whole hit lands on Wuppy; past its block and HP the rest is spent.
                        int wb = Math.Min(wuppyBlock, dmg); wuppyBlock -= wb;
                        wuppyHpLeft -= Math.Min(wuppyHpLeft, dmg - wb);
                        mitigated += dmg;
                        continue;
                    }
                    int rest = dmg;
                    if (it.Blockable)
                    {
                        int b = Math.Min(block, rest); block -= b; rest -= b; mitigated += b;
                        if (attack)
                        {
                            int ob = Math.Min(ostyBlock, rest); ostyBlock -= ob; rest -= ob; mitigated += ob;
                            int oh = Math.Min(ostyHpLeft, rest); ostyHpLeft -= oh; rest -= oh; mitigated += oh;
                        }
                    }
                    if (rest > 0)
                    {
                        if (relics.ReductionFirst) rest = Math.Min(Math.Max(0, rest - relics.Reduction), budget);
                        else rest = Math.Max(0, Math.Min(rest, budget) - relics.Reduction);
                    }
                    if (rest > 0 && buffer > 0) { buffer--; rest = 0; }
                    if (limited) budget -= rest;   // Beating Remnant counts the HP actually lost
                    loss += rest;
                }
            }
        }
        return new Outcome(incoming, mitigated, loss, block, ostyHp - ostyHpLeft, wuppy.Hp - wuppyHpLeft);
    }
}
