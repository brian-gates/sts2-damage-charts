using System;

namespace STS2_DamageCharts.Tracking;

// Buffered: an enemy's (or self) hit on a player that the player's Buffer cancelled. Amount is the HP it would have
// cost (DamageResult.IntendedHpLoss); it is not HP lost, so it is never part of the dealt/taken/absorbed/healed sums.
internal enum HitKind { Dealt = 0, Taken = 1, Absorbed = 2, Healed = 3, Potion = 4, Buffered = 5 }

// How a combat log line is drawn: plain, as HP lost (red) or as HP a Buffer saved (highlighted).
internal enum LogTone { Plain, Taken, Buffered }

// One combat-log event as data. The in-game log line and the shared fight log are both derived from it.
// Slot: the player the event belongs to (dealer, one who lost HP, pet owner, healed player, potion user).
// SourceSlot/TargetSlot are -1 unless that side is a player. Amount is -1 for potions.
// Source/Target keep the display name the log shows today, even for a player side, so the line is unchanged
// (the tracker's per-slot labels differ from the in-fight names: "you", character title only).
internal readonly record struct Hit(
    int Round, HitKind Kind, int Slot,
    string? Source, string? SourceId, int SourceSlot,
    string? Target, int TargetSlot, long Amount)
{
    public bool IsTaken => Kind == HitKind.Taken || Kind == HitKind.Absorbed;

    public LogTone Tone => Kind == HitKind.Buffered ? LogTone.Buffered : IsTaken ? LogTone.Taken : LogTone.Plain;

    // The line the in-game log shows. playerName is only a fallback for a player side with no stored name.
    public string Text(Func<int, string> playerName)
    {
        string src = Source ?? (SourceSlot >= 0 ? playerName(SourceSlot) : "?");
        string tgt = Target ?? (TargetSlot >= 0 ? playerName(TargetSlot) : "?");
        return Kind switch
        {
            HitKind.Healed => $"{tgt} healed  {Amount}",
            HitKind.Potion => Target == null && TargetSlot < 0 ? $"{src} used" : $"{src} used → {tgt}",
            HitKind.Buffered => $"{src} → {tgt}  {Amount} (Buffer)",
            _ => $"{src} → {tgt}  {Amount}",
        };
    }
}
