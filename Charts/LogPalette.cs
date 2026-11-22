using System;

namespace STS2_DamageCharts.Charts;

// Pure (unit-tested): the combat log's amount text colors, shared by the compact summary and the full view, chosen
// for at least 4.5:1 contrast (WCAG AA) against the panels they sit on. Hex "rrggbb"; the views build Godot
// colors from them once. Bars keep their own fills (they carry no text).
internal static class LogPalette
{
    public const string Dealt = "efe6cf";    // cream, as the line text
    public const string Taken = "ff8a80";    // brighter than the old ff5555 / ef6a62
    public const string Saved = "a8d8ff";    // Buffer
    public const string Healed = "9fe39b";

    // The backgrounds amounts are drawn on: the card / sidebar panel, its lighter track tint, the summary's dark
    // panel and the Taken tile.
    public const string CardPanel = "12222a", Track = "1d2f36", SummaryPanel = "0a0d14", TakenTile = "2a1719";

    // WCAG relative luminance of an sRGB hex color.
    public static double Luminance(string hex)
    {
        double Lin(int i)
        {
            double c = Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(0) + 0.7152 * Lin(2) + 0.0722 * Lin(4);
    }

    // WCAG contrast ratio, 1 (none) to 21 (black on white).
    public static double Contrast(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
