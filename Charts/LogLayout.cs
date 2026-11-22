using System;
using System.Collections.Generic;
using STS2_DamageCharts.Tracking;

namespace STS2_DamageCharts.Charts;

internal enum LogRowKind { Line, RoundDivider, FightDivider }

// One display row of the combat log: a line (Entry = index into the log) or a divider (Number = round or fight).
internal readonly record struct LogRow(LogRowKind Kind, int Entry, int Number);

// Pure (unit-tested) layout math for the combat log's rows, amount bars and round dividers.
internal static class LogLayout
{
    // The log's display rows, oldest first, into `rows` (cleared; reused by the caller every frame). A round
    // divider precedes the first line of each round; a fight separator (run recap) becomes a fight divider and
    // starts round tracking over, so the next fight's Round 1 gets its divider.
    public static void Rows(IReadOnlyList<LogEntry> log, List<LogRow> rows)
    {
        rows.Clear();
        int lastRound = int.MinValue;
        for (int i = 0; i < log.Count; i++)
        {
            var e = log[i];
            if (e.IsFightSeparator)
            {
                rows.Add(new LogRow(LogRowKind.FightDivider, i, e.Fight));
                lastRound = int.MinValue;
                continue;
            }
            if (e.Round != lastRound) { rows.Add(new LogRow(LogRowKind.RoundDivider, -1, e.Round)); lastRound = e.Round; }
            rows.Add(new LogRow(LogRowKind.Line, i, e.Round));
        }
    }

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

    // The bars' full scale: a share of the row, so the largest bar is a backdrop and text can run past it.
    public const float BarMaxFrac = 0.45f;
    public static float BarMaxWidth(float rowWidth) => Math.Max(0f, rowWidth) * BarMaxFrac;

    // Width of a line's bar: proportional to amount / max over maxWidth, never past it; any positive amount
    // shows at least minWidth so a 1-HP hit is still visible. 0 for nothing.
    public static float BarWidth(long amount, long max, float maxWidth, float minWidth = 2f)
    {
        if (amount <= 0 || maxWidth <= 0f) return 0f;
        float w = maxWidth * (float)Math.Min(1.0, amount / (double)Math.Max(1, max));
        return Math.Min(maxWidth, Math.Max(minWidth, w));
    }

    // The largest font size <= px (down to minPx) whose measured width fits maxW. measure(size) is the text's width
    // at that size. Never returns px for a column that can't hold the text: a zero or negative width gets minPx.
    public static int FitSize(Func<int, float> measure, int px, float maxW, int minPx = 9)
    {
        if (px <= minPx) return px;
        if (maxW <= 0f) return minPx;
        float w = measure(px);
        if (w <= maxW) return px;
        int size = Math.Max(minPx, Math.Min(px - 1, (int)Math.Floor(px * maxW / Math.Max(1f, w))));
        while (size > minPx && measure(size) > maxW) size--;
        return size;
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
