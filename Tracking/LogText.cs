using System;

namespace STS2_DamageCharts.Tracking;

// Pure (unit-tested): the in-game combat log's display text for a hit, trimmed of what the icon, bar and context
// already say. Display only: Hit.Text, the stats file and the site keep the full line.
//   - taken: "Leaf Slime (M)" + "8" when a single player took it; co-op keeps "→ <player>";
//   - absorbed: always names the pet ("Cultist → Osty");
//   - buffered: "Twig Slime (M)" + "11 saved" (co-op keeps the player);
//   - healed: "" + "+6" (co-op: "<player>" + "+6");
//   - dealt: "Strike" + "6" when the fight has one enemy (showTarget false), else "Strike → Cultist";
//   - potion: the full line, no amount.
internal static class LogText
{
    public static (string Lead, string? Amount) Format(in Hit h, Func<int, string> playerName, bool coop, bool showTarget)
    {
        string src = h.Source ?? (h.SourceSlot >= 0 ? playerName(h.SourceSlot) : "?");
        string tgt = h.Target ?? (h.TargetSlot >= 0 ? playerName(h.TargetSlot) : "?");
        bool selfTarget = !coop && h.TargetSlot >= 0 && h.TargetSlot == h.Slot;
        return h.Kind switch
        {
            HitKind.Taken => (selfTarget ? src : $"{src} → {tgt}", h.Amount.ToString()),
            HitKind.Absorbed => ($"{src} → {tgt}", h.Amount.ToString()),
            HitKind.Buffered => (selfTarget ? src : $"{src} → {tgt}", $"{h.Amount} saved"),
            HitKind.Healed => (coop ? tgt : "", $"+{h.Amount}"),
            HitKind.Dealt => (showTarget ? $"{src} → {tgt}" : src, h.Amount.ToString()),
            _ => (h.Text(playerName), null),
        };
    }

    // True when every dealt line in the log hits the same target name (one enemy): dealt lines then drop it.
    public static bool SingleDealtTarget(System.Collections.Generic.IReadOnlyList<LogEntry> log)
    {
        string? first = null;
        for (int i = 0; i < log.Count; i++)
        {
            if (log[i].Kind != HitKind.Dealt || log[i].Target == null) continue;
            if (first == null) first = log[i].Target;
            else if (!string.Equals(first, log[i].Target, StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
