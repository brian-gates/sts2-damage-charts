using System;
using STS2_DamageCharts.Forecast;

namespace STS2_DamageCharts.Live;

// Maps the game's intent (the forecast's IntentThreat plus the intent's type name and IntentType text) onto the
// live state's five kinds. Pure, so it is tested without the game.
internal static class LiveIntentKind
{
    public static string Of(IntentThreat threat, string? typeName, string? intentType)
    {
        switch (threat)
        {
            case IntentThreat.Attack:
            case IntentThreat.DeathBlow: return "attack";
            case IntentThreat.Unknown: return "unknown";
        }
        string text = (typeName ?? "") + " " + (intentType ?? "");
        // "Debuff" contains "buff", so it is checked first. A status card shoved into the deck is a debuff too.
        if (Has(text, "Debuff") || Has(text, "Status")) return "debuff";
        if (Has(text, "Buff")) return "buff";
        if (Has(text, "Defend")) return "defend";
        return "unknown";
    }

    // Lower is more informative: when a move has several intents, the lowest rank is the one shown.
    public static int Rank(string kind) => kind switch
    {
        "attack" => 0,
        "unknown" => 1,
        "debuff" => 2,
        "buff" => 3,
        "defend" => 4,
        _ => 5,
    };

    private static bool Has(string s, string sub) => s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;
}
