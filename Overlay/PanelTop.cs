namespace STS2_DamageCharts.Overlay;

// Pure (unit-tested): the lowest a panel's top edge may sit, in viewport px, so the game's top bar and relic
// row never draw over the strip players grab. Used by every always-on panel, for drags and per-frame layout.
internal static class PanelTop
{
    // Without the relic row: below the top bar, ~8% of viewport height (9% leaves a hair), at least 48 px.
    public static float Fallback(float viewportHeight) => System.Math.Max(48f, viewportHeight * 0.09f);

    // relicRowBottom: the relic row's bottom edge (viewport px) when it is showing, else null.
    public static float Inset(float viewportHeight, float? relicRowBottom)
    {
        float inset = Fallback(viewportHeight);
        return relicRowBottom is float b && float.IsFinite(b) ? System.Math.Max(inset, b) : inset;
    }
}
