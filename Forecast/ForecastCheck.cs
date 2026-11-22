using System;
using System.Collections.Generic;

namespace STS2_DamageCharts.Forecast;

// One forecast item as a miss records it: a short kind ("attack", "dot", ...), its label and numbers.
internal readonly record struct ForecastMissItem(string Kind, string Label, int Hits, int PerHit, bool Conditional);

// One enemy turn where the local player's real HP loss fell outside the frozen forecast's range. Block, Osty and
// Wuppy are what the forecast assumed (Block includes end-of-turn block); Items are at most MaxItems.
internal sealed record ForecastMiss(int Round, int Min, int Max, int Actual, bool Hidden, int Block, int Buffer,
    bool Intangible, int Osty, int Wuppy, IReadOnlyList<ForecastMissItem> Items);

// Checks the frozen forecast against what really happened, for the run stats file. Pure: the mod calls
// Freeze when the planning phase ends (with the local player's HP taken so far this fight), RoundStarted when
// the next round begins, and CombatEnded when the fight ends first (death, victory, escape), which drops the
// pending check. Only the local player's forecast is checked. Game thread only.
internal sealed class ForecastCheck
{
    public const int MaxMisses = 20;   // the run file holds at most 20 over all fights; a fight never needs more
    public const int MaxItems = 20;

    private (int Round, ForecastResult Result, ForecastInputs Inputs, long Taken)? _frozen;
    private readonly List<ForecastMiss> _misses = new();

    // This fight's misses, oldest first (at most MaxMisses).
    public IReadOnlyList<ForecastMiss> Misses => _misses;
    public bool HasPending => _frozen != null;

    public void BeginFight() { _frozen = null; _misses.Clear(); }

    // The forecast froze in `round`. A later freeze in the same round (an extra turn) replaces this one.
    public void Freeze(int round, ForecastResult result, ForecastInputs inputs, long takenSoFar) =>
        _frozen = (round, result, inputs, takenSoFar);

    // Round `round` started: the pending check (from an earlier round) is evaluated with actual = HP taken
    // since the freeze. Returns the miss, if it was one. A round number that isn't later keeps it pending.
    public ForecastMiss? RoundStarted(int round, long takenNow)
    {
        if (_frozen is not { } f || round <= f.Round) return null;
        _frozen = null;
        var miss = Evaluate(f.Round, f.Result, f.Inputs, takenNow - f.Taken);
        if (miss != null && _misses.Count < MaxMisses) _misses.Add(miss);
        return miss;
    }

    public void CombatEnded() => _frozen = null;

    // A miss: less HP lost than the guaranteed minimum, or more than the maximum when no intent was hidden
    // (a hidden intent may legitimately add damage the forecast could not show).
    public static bool IsMiss(ForecastResult r, long actual) =>
        actual < r.MinLoss || (actual > r.MaxLoss && !r.HasHidden);

    public static ForecastMiss? Evaluate(int round, ForecastResult r, ForecastInputs inputs, long actual)
    {
        actual = Math.Max(0, actual);
        if (!IsMiss(r, actual)) return null;
        int min = Math.Max(0, r.MinLoss);
        var items = new List<ForecastMissItem>(Math.Min(MaxItems, r.Items.Count));
        foreach (var it in r.Items)
        {
            if (items.Count >= MaxItems) break;
            items.Add(new ForecastMissItem(KindName(it.Kind), it.Label ?? "", Math.Max(0, it.Hits), Math.Max(0, it.DamagePerHit), it.Conditional));
        }
        return new ForecastMiss(Math.Max(0, round), min, Math.Max(min, r.MaxLoss), (int)Math.Min(int.MaxValue, actual),
            r.HasHidden, Math.Max(0, r.Block), inputs.Buffer, inputs.Intangible, Math.Max(0, r.OstyAbsorb), inputs.WuppyHp, items);
    }

    public static string KindName(ForecastKind kind) => kind switch
    {
        ForecastKind.Attack => "attack",
        ForecastKind.Dot => "dot",
        ForecastKind.SelfCard => "self",
        ForecastKind.NonAttackHpLoss => "hp_loss",
        ForecastKind.Hidden => "hidden",
        _ => "other",
    };
}
