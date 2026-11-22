using System;

namespace STS2_DamageCharts.Charts;

// Pure (unit-tested) layout math for the full-screen breakdown's lower half: the Damage / Round chart fills the
// left column and the combat log the right one, both from below the source columns to their own bottoms (the
// chart stops above the close button, bottom-left; the log runs further down on the right).
internal static class DetailLayout
{
    // How low the source columns (with the potions row) may reach so the chart keeps chartMinH and the log logMinH
    // below them, each after a vgap.
    public static float ColumnsLimit(float chartBottom, float logBottom, float chartMinH, float logMinH, float vgap, float potH)
        => Math.Min(chartBottom - chartMinH, logBottom - logMinH) - vgap - potH;

    // Below the columns (colBottom): the chart's top and height (0 = dropped, too little room), and the log's top.
    public static (float ChartTop, float ChartH, float LogTop) Split(float colBottom, float vgap, float chartBottom, float chartMinH)
    {
        float t = colBottom + vgap;
        float h = chartBottom - t;
        return (t, h >= chartMinH && h > 0f ? h : 0f, t);
    }

    // Width of one chart cell (one player's bars in a round group): spread over the plot as if there were at
    // least minGroups groups, so a fight's first rounds aren't stretched across the whole plot, capped at maxCell.
    public static float CellWidth(float plotW, int groups, int players, float maxCell, int minGroups = 8)
    {
        int slots = Math.Max(1, Math.Max(groups, minGroups) * Math.Max(1, players));
        return Math.Max(0f, Math.Min(maxCell, plotW / slots));
    }
}
