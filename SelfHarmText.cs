using System.Text.RegularExpressions;

namespace STS2_DamageCharts;

// Classifies resolved card text as costing the player HP. Pure (no Godot/game refs) so it is unit-tested.
//
// Only a clause whose subject is the player counts: an imperative at the start of the text or of a clause
// ("Lose 6 HP.", "Exhaust. Take 3 damage.", "..., if this is in your hand, lose 2 HP.") or an explicit
// "you take"/"you lose". Text where someone else takes the damage ("they take 9 damage", "All enemies take
// 5 damage", "it takes 4 damage") is not self-harm, nor is a trigger condition ("Whenever you lose HP, ...").
internal static class SelfHarmText
{
    // take/lose ... damage/HP/health within one sentence, where take/lose is either the first word of a
    // clause (text start, or after . ! ? ; : , or a line break) or follows "you" that isn't a
    // whenever/when/if/after/each time/every time condition. Group "n" spans the self clause up to the
    // damage/HP word; the amount is the first number in it.
    private const string Tail = @"(?:take|lose)\b(?<n>[^.!?;\n]{0,40}?)\b(?:damage|hp|health)\b";
    private static readonly Regex Rx = new(
        @"(?:^|[.!?;:,\n])\s*" + Tail
        + @"|(?<!\b(?:whenever|when|if|after|each\s+time|every\s+time)\s+)\byou\s+" + Tail,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex NumRx = new(@"\d+");

    // True when the text has a self-harm clause; amount is the number in that clause (0 when it has none,
    // e.g. "lose HP equal to ..."; callers then fall back to the card's fields).
    public static bool TryMatch(string? desc, out int amount)
    {
        amount = 0;
        if (string.IsNullOrEmpty(desc)) return false;
        var m = Rx.Match(desc);
        if (!m.Success) return false;
        var n = NumRx.Match(m.Groups["n"].Value);
        if (n.Success && int.TryParse(n.Value, out int v)) amount = v;
        return true;
    }

    public static bool IsSelfHarm(string? desc) => TryMatch(desc, out _);
}
