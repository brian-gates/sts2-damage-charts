using System;
using System.Text;
using MegaCrit.Sts2.Core.Localization;

namespace STS2_DamageCharts;

internal static class TextHelper
{
    // Safely resolve a possibly-LocString getter to a plain string; null on any error.
    public static string? SafeGetText(Func<object?> getter)
    {
        try
        {
            var result = getter();
            if (result == null) return null;
            if (result is LocString loc) return StripRichTextTags(loc.GetFormattedText());
            return result.ToString();
        }
        catch { return null; }
    }

    public static string StripRichTextTags(string text)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '[')
            {
                int end = text.IndexOf(']', i);
                if (end >= 0) { i = end + 1; continue; }
            }
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

    // Turn a source class name into a readable label:
    //   "PoisonPower" -> "Poison", "TheBombPower" -> "The Bomb", "LightningOrb" -> "Lightning Orb".
    public static string HumanizeSourceType(string typeName)
    {
        string name = typeName;
        if (name.EndsWith("Power", StringComparison.Ordinal) && name.Length > "Power".Length)
            name = name[..^"Power".Length];

        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
