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
    public readonly int Buffer;       // Buffer stacks: each cancels one HP loss (after block and Osty)
    public readonly bool Intangible;  // every hit on the player deals and costs at most 1

    // ostyHp < 0: the HP/block split is unknown, so all of OstyAbsorb is treated as HP.
    public ForecastInputs(int currentHp, int maxHp, int block, int ostyAbsorb, bool hasHidden, IReadOnlyList<ForecastItem> items,
                          int endTurnBlock = 0, int ostyHp = -1, int buffer = 0, bool intangible = false)
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
        // Hits resolve one at a time, in this order:
        //   1. self damage (the player's own powers/cards, e.g. Disintegration, Infection, "lose N HP" DoTs):
        //      during or at the end of the player's turn, before enemies act. "Take N damage" spends the
        //      player's block only, never Osty; "lose N HP" bypasses block.
        //   2. enemy items, in intent order: attacks (incl. death blows) hit player block, then Osty (block,
        //      then HP), then the player; unblockable ones go straight to HP.
        // Block and Osty are pools, so the order only matters for Buffer (BufferPower.ModifyHpLostAfterOstyLate):
        // each stack cancels the next hit that would still cost HP after block and Osty.
        // Intangible caps every hit on the player at 1 (IntangiblePower.ModifyDamageCap before block, and
        // ModifyHpLostAfterOsty for "lose HP"). Enemy intents already show the capped number (the game's
        // preview runs the cap); self damage and HP loss items don't, so the cap is applied here to every hit.
        // End-of-turn block lands before the enemy turn, so it counts like block already on the player.
        int block = Math.Max(0, inp.Block) + Math.Max(0, inp.EndTurnBlock);
        int osty = Math.Max(0, inp.OstyAbsorb);
        int ostyHp = Math.Min(Math.Max(0, inp.OstyHp), osty);

        var g = Simulate(inp.Items, block, osty, ostyHp, inp.Buffer, inp.Intangible, includeConditional: false);
        var w = Simulate(inp.Items, block, osty, ostyHp, inp.Buffer, inp.Intangible, includeConditional: true);

        // Spare block in the worst case: the player's own block left after self damage and attacks (spare
        // Osty isn't block).
        return new ForecastResult(g.Incoming, g.Mitigated, g.Loss, w.Loss,
            block, osty, inp.CurrentHp, inp.MaxHp, inp.HasHidden, inp.Items, w.BlockLeft, ostyHp, w.OstyHpLoss);
    }

    private readonly record struct Outcome(int Incoming, int Mitigated, int Loss, int BlockLeft, int OstyHpLoss);

    private static bool IsSelf(ForecastItem it) => it.Kind == ForecastKind.Dot || it.Kind == ForecastKind.SelfCard;

    private static Outcome Simulate(IReadOnlyList<ForecastItem> items, int block, int osty, int ostyHp, int buffer,
        bool intangible, bool includeConditional)
    {
        int ostyBlock = osty - ostyHp, ostyHpLeft = ostyHp;
        int incoming = 0, mitigated = 0, loss = 0;
        for (int phase = 0; phase < 2; phase++)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.Kind == ForecastKind.Hidden || it.Total <= 0) continue;
                if (IsSelf(it) != (phase == 0)) continue;
                if (it.Conditional && !includeConditional) continue;
                bool attack = !IsSelf(it) && it.Blockable;
                for (int h = 0; h < it.Hits; h++)
                {
                    int dmg = intangible ? Math.Min(1, it.DamagePerHit) : it.DamagePerHit;
                    if (dmg <= 0) continue;
                    if (!it.Conditional) incoming += dmg;
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
                    if (rest > 0 && buffer > 0) { buffer--; rest = 0; }
                    loss += rest;
                }
            }
        }
        return new Outcome(incoming, mitigated, loss, block, ostyHp - ostyHpLeft);
    }
}
