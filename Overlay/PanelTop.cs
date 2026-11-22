namespace STS2_DamageCharts.Overlay;

// Pure (unit-tested): the lowest a panel's top edge may sit, in viewport px, so the game's top bar and relic
// row never draw over the strip players grab. Used by every always-on panel, for drags and per-frame layout.
internal static class PanelTop
{
    // Without the relic row: below the top bar, ~8% of viewport height (9% leaves a hair), at least 48 px.
    public static float Fallback(float viewportHeight) => System.Math.Max(48f, viewportHeight * 0.09f);

    // Deepest a measured relic row bottom is believed: even several wrapped rows of relics end well above this.
    // A deeper reading is a mismeasure (a container rect much taller than its relics), which would pin every
    // panel to mid-screen, so it is ignored.
    public const float MaxRelicBottomFrac = 0.40f;

    // relicRowBottom: the relic row's bottom edge (viewport px) when it is showing, else null.
    public static float Inset(float viewportHeight, float? relicRowBottom)
    {
        float inset = Fallback(viewportHeight);
        if (relicRowBottom is not float b || !float.IsFinite(b) || b > viewportHeight * MaxRelicBottomFrac) return inset;
        return System.Math.Max(inset, b);
    }
}
