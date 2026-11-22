using System;
using System.Collections.Generic;

namespace STS2_DamageCharts.Forecast;

// One forecast item as a miss records it: a short kind ("attack", "dot", ...), its label and numbers.
internal readonly record struct ForecastMissItem(string Kind, string Label, int Hits, int PerHit, bool Conditional);

// One enemy turn where the local player's real HP loss fell outside the frozen forecast's range. Block, Osty and
// Wuppy are what the forecast assumed (Block includes end-of-turn block); Items are at most MaxItems.
internal sealed record ForecastMiss(int Round, int Min, int Max, int Actual, bool Hidden, int Block, int Buffer,
    bool Intangible, int Osty, int Wuppy, IReadOnlyList<ForecastMissItem> Items);

// Checks the frozen forecast against what really happened, for the run stats file. Pure. The mod calls Observe
// every combat tick:
//   - planning ends (the player side has all ended their turn): the forecast freezes with the local player's HP
//     taken so far, but only when the mod computed it right up to that moment; a stale or missing forecast (the
//     forecast panel was hidden, the breakdown was open) skips the round rather than check the wrong numbers;
//   - the local player's Play phase starts in a later round: actual = HP taken since the freeze. That is after the
//     player side's turn start (PlayerTurnPhase.Start covers Hook.AfterSideTurnStart, where Poison ticks), so
//     start-of-turn Poison, which the forecast includes, counts in the round it was forecast for.
// CombatEnded (death, victory, escape) drops the pending check. Only the local player's forecast is checked.
// Game thread only.
internal sealed class ForecastCheck
{
    public const int MaxMisses = 20;   // the run file holds at most 20 over all fights; a fight never needs more
    public const int MaxItems = 20;

    private (int Round, ForecastResult Result, ForecastInputs Inputs, long Taken)? _frozen;
    private readonly List<ForecastMiss> _misses = new();
    private bool _planningLast, _localPlayLast;   // what Observe saw last tick

    // This fight's misses, oldest first (at most MaxMisses).
    public IReadOnlyList<ForecastMiss> Misses => _misses;
    public bool HasPending => _frozen != null;

    public void BeginFight() { _frozen = null; _misses.Clear(); _planningLast = false; _localPlayLast = false; }

    // One combat tick. `planning`: the player side is still planning (ForecastReader.IsPlanningPhase). `localPlay`:
    // the local player is in their Play phase. `fresh`: the forecast computed during this planning phase, only when
    // it was computed up to now (null otherwise). `taken` reads the local player's HP lost this fight; it is only
    // called on an edge. Returns the miss recorded this tick, if any.
    public ForecastMiss? Observe(int round, bool planning, bool localPlay, Func<long> taken,
        (ForecastResult Result, ForecastInputs Inputs)? fresh)
    {
        ForecastMiss? miss = null;
        if (localPlay && !_localPlayLast && _frozen != null) miss = PlayStarted(round, taken());
        if (!planning && _planningLast)
        {
            if (fresh is { } f) Freeze(round, f.Result, f.Inputs, taken());
            else _frozen = null;   // not seen on time: skip this round
        }
        _planningLast = planning;
        _localPlayLast = localPlay;
        return miss;
    }

    // The forecast froze in `round`. A later freeze in the same round (an extra turn) replaces this one.
    public void Freeze(int round, ForecastResult result, ForecastInputs inputs, long takenSoFar) =>
        _frozen = (round, result, inputs, takenSoFar);

    // The local player's Play phase started in `round`: the pending check (from an earlier round) is evaluated
    // with actual = HP taken since the freeze. Returns the miss, if it was one. A round number that isn't later
    // (an extra turn) keeps it pending.
    public ForecastMiss? PlayStarted(int round, long takenNow)
    {
        if (_frozen is not { } f || round <= f.Round) return null;
        _frozen = null;
        var miss = Evaluate(f.Round, f.Result, f.Inputs, takenNow - f.Taken);
        if (miss != null && _misses.Count < MaxMisses) _misses.Add(miss);
        return miss;
    }

    public void CombatEnded() => _frozen = null;

    // A miss: less HP lost than the guaranteed minimum, or more than the maximum when no intent was hidden
    // (a hidden intent may legitimately add damage the forecast could not show). The player can't lose more HP
    // than they had, so the minimum is capped at the HP at the freeze; a lethal forecast is never "over" (a
    // revive, or a co-op partner fighting on, lets more HP be lost after the killing blow).
    public static bool IsMiss(ForecastResult r, long actual)
    {
        long min = Math.Min(r.MinLoss, Math.Max(0, r.CurrentHp));
        return actual < min || (actual > r.MaxLoss && !r.HasHidden && !r.IsLethal);
    }

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
