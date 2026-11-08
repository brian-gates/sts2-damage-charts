using System;
using System.Collections.Generic;

namespace STS2_DamageCharts;

internal enum IntentThreat { None, Attack, DeathBlow, Unknown }

// How the forecast treats one enemy intent. Pure (no Godot/game refs) so it is unit-tested; ForecastReader
// feeds it the intent's type-name chain (most derived first) and its IntentType text.
//
// DeathBlowIntent is a final attack that bypasses the usual "is it an Attack intent" name check. The
// Waterfall Giant shows it once Doom/damage takes it to 0 HP: it enters an "about to blow" state
// (AboutToBlowMove), then its ExplodeMove hits the player. Gas Bomb's ExplodeMove is the same shape.
internal static class IntentRules
{
    public static IntentThreat Classify(IReadOnlyList<string> typeChain, string? intentType)
    {
        string it = intentType ?? "";
        foreach (var t in typeChain)
            if (Has(t, "Hidden") || Has(t, "Unknown")) return IntentThreat.Unknown;
        if (Has(it, "Hidden") || Has(it, "Unknown")) return IntentThreat.Unknown;
        foreach (var t in typeChain)
            if (Has(t, "DeathBlow")) return IntentThreat.DeathBlow;
        if (Has(it, "DeathBlow")) return IntentThreat.DeathBlow;
        foreach (var t in typeChain)
            if (Has(t, "Attack")) return IntentThreat.Attack;
        if (Has(it, "Attack")) return IntentThreat.Attack;
        return IntentThreat.None;
    }

    public enum Outcome { Skip, Add, Hidden }

    // What one intent contributes. `alive` is the enemy's IsAlive: an enemy the game keeps in combat at 0 HP
    // only threatens through a death blow (a dead enemy's leftover intents are not moves it will make).
    // An ordinary attack with no readable number stays a non-threat (a modified 0 is a real answer); a death
    // blow we can't put a number on is hidden, so the panel never claims Safe while it is pending.
    public static Outcome Resolve(IntentThreat threat, bool alive, int perHit)
    {
        switch (threat)
        {
            case IntentThreat.DeathBlow: return perHit > 0 ? Outcome.Add : Outcome.Hidden;
            case IntentThreat.Attack: return alive && perHit > 0 ? Outcome.Add : Outcome.Skip;
            case IntentThreat.Unknown: return alive ? Outcome.Hidden : Outcome.Skip;
            default: return Outcome.Skip;
        }
    }

    private static bool Has(string s, string sub) => s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;
}
