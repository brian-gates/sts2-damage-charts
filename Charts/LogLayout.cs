using System;
using System.Collections.Generic;
using STS2_DamageCharts.Tracking;

namespace STS2_DamageCharts.Charts;

// Pure (unit-tested) layout math for the combat log's amount bars and round dividers.
internal static class LogLayout
{
    // Lines with an HP amount get a bar; potion lines and text-only lines don't.
    public static bool HasBar(HitKind? kind) => kind is HitKind k && k != HitKind.Potion;

    // The largest single amount among the bar lines (the bars' scale), at least 1.
    public static long MaxAmount(IReadOnlyList<LogEntry> log)
    {
        long max = 1;
        for (int i = 0; i < log.Count; i++)
            if (HasBar(log[i].Kind) && log[i].Amount > max) max = log[i].Amount;
        return max;
    }

    // Width of a line's bar: proportional to amount / max over maxWidth, never past it; any positive amount
    // shows at least minWidth so a 1-HP hit is still visible. 0 for nothing.
    public static float BarWidth(long amount, long max, float maxWidth, float minWidth = 2f)
    {
        if (amount <= 0 || maxWidth <= 0f) return 0f;
        float w = maxWidth * (float)Math.Min(1.0, amount / (double)Math.Max(1, max));
        return Math.Min(maxWidth, Math.Max(minWidth, w));
    }

    // A round divider: the label centered in [left, right] with a line on each side, `gap` px from the label.
    // Lines shorter than 4 px are dropped (width 0).
    public static (float LabelX, float LeftLineX, float LeftLineW, float RightLineX, float RightLineW) Divider(
        float left, float right, float labelW, float gap)
    {
        float width = Math.Max(0f, right - left);
        labelW = Math.Min(Math.Max(0f, labelW), width);
        float labelX = left + (width - labelW) * 0.5f;
        float lw = labelX - gap - left;
        float rx = labelX + labelW + gap;
        float rw = right - rx;
        return (labelX, left, lw >= 4f ? lw : 0f, rx, rw >= 4f ? rw : 0f);
    }
}
