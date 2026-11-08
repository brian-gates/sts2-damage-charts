using System;
using System.Collections.Generic;

namespace STS2_DamageCharts;

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

    public ForecastItem(string label, ForecastKind kind, int hits, int damagePerHit, bool blockable, bool conditional)
    {
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

    // ostyHp < 0: the HP/block split is unknown, so all of OstyAbsorb is treated as HP.
    public ForecastInputs(int currentHp, int maxHp, int block, int ostyAbsorb, bool hasHidden, IReadOnlyList<ForecastItem> items,
                          int endTurnBlock = 0, int ostyHp = -1)
    {
        CurrentHp = currentHp;
        MaxHp = maxHp;
        Block = block;
        OstyAbsorb = ostyAbsorb;
        HasHidden = hasHidden;
        Items = items ?? Array.Empty<ForecastItem>();
        EndTurnBlock = endTurnBlock;
        int absorb = Math.Max(0, ostyAbsorb);
        OstyHp = ostyHp < 0 ? absorb : Math.Min(ostyHp, absorb);
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

    public ForecastResult(int incoming, int mitigated, int minLoss, int maxLoss,
        int block, int ostyAbsorb, int currentHp, int maxHp, bool hasHidden, IReadOnlyList<ForecastItem> items,
        int overblock = 0, int ostyHp = 0, int ostyHpLoss = 0)
    {
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
    public bool IsLethal => CurrentHp > 0 && MaxLoss >= CurrentHp;
}

internal static class DamageForecast
{
    public static ForecastResult Compute(ForecastInputs inp)
    {
        int guaranteedAttack = 0, guaranteedUnblockable = 0, condAttack = 0, condUnblockable = 0;

        var items = inp.Items;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Kind == ForecastKind.Hidden) continue;
            int t = it.Total;
            if (t <= 0) continue;
            if (it.Conditional)
            {
                if (it.Blockable) condAttack += t; else condUnblockable += t;
            }
            else
            {
                if (it.Blockable) guaranteedAttack += t; else guaranteedUnblockable += t;
            }
        }

        // Attacks are absorbed by the combined pool of player block + Osty (a damage sink in front of the
        // player); DoTs bypass both. The player's loss is order-independent, so we pool them. End-of-turn
        // block lands before the enemy turn, so it counts like block already on the player.
        // For Osty's own loss the order matters. The game applies the player's block first and then redirects
        // the remaining HP loss to Osty (sts2.xml: ModifyHpLostBeforeOsty "runs ... after block is applied ...
        // BEFORE damage is redirected from Necrobinder to Osty", via DieForYouPower). Osty's block is assumed
        // to soak the redirected damage before its HP, as the pool already assumes.
        int block = Math.Max(0, inp.Block) + Math.Max(0, inp.EndTurnBlock);
        int osty = Math.Max(0, inp.OstyAbsorb);
        int pool = block + osty;

        int mitigated = Math.Min(pool, guaranteedAttack);
        int guaranteedAttackLoss = guaranteedAttack - mitigated;
        int minLoss = guaranteedAttackLoss + guaranteedUnblockable;

        int poolLeft = pool - mitigated;
        int condAttackLoss = Math.Max(0, condAttack - poolLeft);
        int maxLoss = minLoss + condAttackLoss + condUnblockable;

        int incoming = guaranteedAttack + guaranteedUnblockable;
        // Spare block in the worst case: the pool left after all blockable attacks, but never more than the
        // player's own block (spare Osty isn't block).
        int overblock = Math.Min(block, Math.Max(0, pool - guaranteedAttack - condAttack));

        // Osty's worst case: every blockable attack lands, the player's block soaks first, then Osty's block,
        // then its HP (capped at the HP it has; the overflow is the player's loss above).
        int ostyHp = Math.Min(Math.Max(0, inp.OstyHp), osty);
        int ostyBlock = osty - ostyHp;
        int pastBlock = Math.Max(0, guaranteedAttack + condAttack - block);
        int ostyHpLoss = Math.Min(ostyHp, Math.Max(0, pastBlock - ostyBlock));

        return new ForecastResult(incoming, mitigated, minLoss, maxLoss,
            block, osty, inp.CurrentHp, inp.MaxHp, inp.HasHidden, items, overblock, ostyHp, ostyHpLoss);
    }
}
