using System;

namespace STS2_DamageCharts;

// Which res:// path is a card's portrait: its final art (CardModel.PortraitPath) when the game ships it, else its
// placeholder art (BetaPortraitPath, e.g. new characters such as The Fool in v0.113). Properties are read by name
// so older game versions without BetaPortraitPath still work. Pure: `exists` is the resource check (the game's
// ResourceLoader.Exists), so this is a path lookup, never a texture load, and works with native_visuals off.
internal static class CardPortrait
{
    private static readonly string[] Props = { "PortraitPath", "BetaPortraitPath" };

    public static string? Pick(object? card, Func<string, bool> exists)
    {
        if (card == null) return null;
        foreach (var prop in Props)
        {
            try
            {
                if (card.GetType().GetProperty(prop)?.GetValue(card) is string p && p.Length > 0 && exists(p)) return p;
            }
            catch { }
        }
        return null;
    }
}
