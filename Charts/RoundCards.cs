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

    // Columns inside a card whose content box starts at `left` and is `width` wide (mockup: 30 | 1fr | 90 | 240,
    // gaps 14). The bar track narrows to 30% of the width on narrow cards; the text gets what is left.
    public static CardColumns Columns(float left, float width, float u)
    {
        float gap = 14f * u, iconCol = 30f * u, amountW = 90f * u;
        float barW = Math.Min(240f * u, Math.Max(0f, width) * 0.30f);
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

    // The sidebar's top sources in `avail` px: every source when they fit at a pitch of at least minPitch (rows
    // spread up to maxPitch), else as many minPitch rows as fit, shared by SplitSources. Pitch includes the gap
    // after a row; the last row needs only rowH of it, so that much more height is allowed.
    public static (int Dealt, int Taken, float Pitch) SidebarSources(float avail, int dealtCount, int takenCount,
        float rowH, float maxPitch, float minPitch)
    {
        int n = Math.Max(0, dealtCount) + Math.Max(0, takenCount);
        if (n == 0 || avail < rowH) return (0, 0, maxPitch);
        float room = avail - rowH;                       // the first row; every further row costs a pitch
        float pitch = n > 1 ? Math.Clamp(room / (n - 1), minPitch, maxPitch) : maxPitch;
        int slots = 1 + (int)Math.Floor(room / pitch + 1e-4f);
        var (d, t) = SplitSources(Math.Min(slots, n), dealtCount, takenCount);
        return (d, t, pitch);
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
