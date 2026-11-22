using System;
using System.Collections.Generic;
using STS2_DamageCharts.Tracking;

namespace STS2_DamageCharts.Charts;

// Pure (unit-tested) layout of the full breakdown's combat log as round cards (layout "E"): one card per round
// (header, dealt|taken split bar, then one table row per line), with the run recap's fight dividers between
// groups. All sizes come from CardMetrics, built from one scale unit u (the 1280x720 mockup's px x 1.5 x the
// UI scale). Positions are in content space: y = 0 at the top of the scrolling list.

internal enum CardItemKind { Card, FightDivider }

// A card (Number = round; First/Count = its log entries; Dealt/Taken = the round's sums over all players) or a
// fight divider (Number = fight; First = the separator entry). Y/H in content px.
internal readonly record struct CardItem(CardItemKind Kind, int Number, int First, int Count, long Dealt, long Taken, float Y, float H);

internal readonly record struct CardMetrics(float PadY, float PadX, float HeaderH, float Gap, float SplitH, float SplitMargin,
    float RowH, float CardGap, float DividerH)
{
    // The mockup's px (card padding 14/18, header 26, flex gap 4, split bar 8 + 6, rows 36, cards 14 apart).
    public static CardMetrics Scaled(float u) => new(14f * u, 18f * u, 26f * u, 4f * u, 8f * u, 6f * u, 36f * u, 14f * u, 36f * u);

    // One scroll step: a row and its gap.
    public float Step => RowH + Gap;
}

// The four table columns of a card row: icon | text | amount (right-aligned) | bar track.
internal readonly record struct CardColumns(float IconX, float TextX, float TextW, float AmountX, float AmountW, float BarX, float BarW);

// The full view's horizontal frame at one viewport width: the scale unit actually used (U, at most the requested
// one), side padding, sidebar width, the sidebar-to-log gap, the log column and the card rows' bar track width.
internal readonly record struct FullFrame(float U, float Pad, float SideW, float Gap, float LogX, float LogW, float BarW);

internal static class RoundCards
{
    public static float CardHeight(int rows, CardMetrics m) =>
        2f * m.PadY + m.HeaderH + m.Gap + m.SplitH + m.SplitMargin + Math.Max(0, rows) * (m.Gap + m.RowH);

    // Top of the card's split bar and of its row i, in the card's own space (0 = card top).
    public static float SplitY(CardMetrics m) => m.PadY + m.HeaderH + m.Gap;
    public static float RowY(int i, CardMetrics m) => SplitY(m) + m.SplitH + m.SplitMargin + m.Gap + i * (m.Gap + m.RowH);

    // The log's cards and fight dividers, oldest first, into `items` (cleared; reused every frame). Returns the
    // content height. Consecutive lines of one round make one card; a fight separator closes the current card.
    public static float Build(IReadOnlyList<LogEntry> log, CardMetrics m, List<CardItem> items)
    {
        items.Clear();
        float y = 0f;
        int i = 0;
        while (i < log.Count)
        {
            if (log[i].IsFightSeparator)
            {
                if (items.Count > 0) y += m.CardGap;
                items.Add(new CardItem(CardItemKind.FightDivider, log[i].Fight, i, 1, 0, 0, y, m.DividerH));
                y += m.DividerH;
                i++;
                continue;
            }
            int round = log[i].Round, first = i;
            long dealt = 0, taken = 0;
            while (i < log.Count && !log[i].IsFightSeparator && log[i].Round == round)
            {
                if (log[i].Kind == HitKind.Dealt) dealt += Math.Max(0, log[i].Amount);
                else if (log[i].Kind == HitKind.Taken) taken += Math.Max(0, log[i].Amount);
                i++;
            }
            if (items.Count > 0) y += m.CardGap;
            float h = CardHeight(i - first, m);
            items.Add(new CardItem(CardItemKind.Card, round, first, i - first, dealt, taken, y, h));
            y += h;
        }
        return y;
    }

    // Mockup widths (px at 1280x720) and the minimums they give way to, in this order, when the viewport is narrow
    // (16:10, 4:3, high ui_scale): the bar track, then the sidebar, then padding and gap; past that, the scale
    // unit itself shrinks so the text column keeps TextMin.
    public const float PadMax = 40f, PadMin = 20f, GapMax = 32f, GapMin = 16f, SideMax = 330f, SideMin = 230f;
    public const float BarMax = 240f, BarMin = 80f, AmountCol = 90f, IconCol = 30f, ColGap = 14f, TextMin = 200f;
    public const float CardPadX = 18f;
    // A card row without its text and bar: the card's side padding, icon column, three column gaps, amount column.
    public const float RowFixed = 2f * CardPadX + IconCol + 3f * ColGap + AmountCol;

    public static FullFrame Frame(float width, float u)
    {
        width = Math.Max(1f, width);
        float minPerU = 2f * PadMin + SideMin + GapMin + RowFixed + BarMin + TextMin;
        u = Math.Max(0.01f, Math.Min(u, width / minPerU));
        // Start at the minimums and hand the slack back in reverse order: padding and gap, sidebar, bar track;
        // whatever is left widens the text column.
        float slack = width / u - minPerU;
        float Give(float range) { float g = Math.Clamp(slack, 0f, range); slack -= g; return g; }
        float spacing = 2f * (PadMax - PadMin) + (GapMax - GapMin);
        float t = Give(spacing) / spacing;
        float pad = PadMin + t * (PadMax - PadMin), gap = GapMin + t * (GapMax - GapMin);
        float side = SideMin + Give(SideMax - SideMin);
        float bar = BarMin + Give(BarMax - BarMin);
        float logX = (pad + side + gap) * u;
        float logW = width - logX - pad * u;
        return new FullFrame(u, pad * u, side * u, gap * u, logX, logW, bar * u);
    }

    // Columns inside a card whose content box starts at `left` and is `width` wide (mockup: 30 | 1fr | 90 | 240,
    // gaps 14). `barW` is the track width (FullFrame.BarW); it narrows further only if the text would go below
    // zero. The text gets what is left.
    public static CardColumns Columns(float left, float width, float u, float barW)
    {
        float gap = ColGap * u, iconCol = IconCol * u, amountW = AmountCol * u;
        barW = Math.Max(0f, Math.Min(barW, width - iconCol - 3f * gap - amountW));
        float barX = left + width - barW;
        float amountX = barX - gap - amountW;
        float textX = left + iconCol + gap;
        float textW = Math.Max(0f, amountX - gap - textX);
        float iconX = left + 2f * u;   // the 26 px icon centered in its 30 px column
        return new CardColumns(iconX, textX, textW, amountX, amountW, barX, barW);
    }

    // The header's dealt|taken split bar: each side's share of `width` (both 0 when the round had neither).
    public static (float DealtW, float TakenW) SplitBar(long dealt, long taken, float width)
    {
        dealt = Math.Max(0, dealt); taken = Math.Max(0, taken);
        long sum = dealt + taken;
        if (sum <= 0 || width <= 0f) return (0f, 0f);
        float d = width * (float)(dealt / (double)sum);
        return (d, width - d);
    }

    // Scrolling in steps of CardMetrics.Step: how many steps fit, the last offset, and the content y at an offset.
    public static int VisibleSteps(float viewH, CardMetrics m) => Math.Max(1, (int)(viewH / Math.Max(1f, m.Step)));
    public static int MaxOffset(float contentH, float viewH, CardMetrics m) =>
        contentH <= viewH ? 0 : (int)Math.Ceiling((contentH - viewH) / Math.Max(1f, m.Step));
    public static float ScrollY(int offset, float contentH, float viewH, CardMetrics m) =>
        Math.Clamp(offset * m.Step, 0f, Math.Max(0f, contentH - viewH));

    // The sidebar's top sources in `avail` px. Every source shows when they fit at a pitch of at least minPitch
    // (rows spread up to maxPitch). Otherwise rows are minPitch apart and the last slot becomes a "+ N more" row
    // (More = the sources not shown, dealt and taken combined); with room for fewer than two slots nothing is
    // shown (D = T = 0, More = all), and the caller leaves out the heading too. Pitch includes the gap after a
    // row; the last row needs only rowH of it.
    public static (int Dealt, int Taken, int More, float Pitch) SidebarPlan(float avail, int dealtCount, int takenCount,
        float rowH, float maxPitch, float minPitch)
    {
        dealtCount = Math.Max(0, dealtCount); takenCount = Math.Max(0, takenCount);
        int n = dealtCount + takenCount;
        if (n == 0) return (0, 0, 0, maxPitch);
        if (avail < rowH) return (0, 0, n, minPitch);
        float room = avail - rowH;                       // the first row; every further row costs a pitch
        if (room + 1e-3f >= (n - 1) * minPitch)
        {
            float pitch = n > 1 ? Math.Min(maxPitch, room / (n - 1)) : maxPitch;
            return (dealtCount, takenCount, 0, pitch);
        }
        int slots = 1 + (int)Math.Floor(room / minPitch + 1e-4f);
        if (slots < 2) return (0, 0, n, minPitch);
        var (d, t) = SplitSources(slots - 1, dealtCount, takenCount);
        return (d, t, n - d - t, minPitch);
    }

    // The sidebar's top source rows: `slots` rows shared by the dealt and taken lists, half each, a side's unused
    // half going to the other.
    public static (int Dealt, int Taken) SplitSources(int slots, int dealtCount, int takenCount)
    {
        slots = Math.Max(0, slots);
        int d = Math.Min(Math.Max(0, dealtCount), (slots + 1) / 2);
        int t = Math.Min(Math.Max(0, takenCount), slots - d);
        d = Math.Min(Math.Max(0, dealtCount), slots - t);
        return (d, t);
    }
}
