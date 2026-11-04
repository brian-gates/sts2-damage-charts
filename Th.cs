using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;

namespace STS2_DamageCharts;

// Idempotent styling for our retained Godot nodes (issue #31). The views re-render every frame, but every
// AddTheme*Override fires NOTIFICATION_THEME_CHANGED and every Text assignment re-parses and re-shapes the
// label, even when the value is identical, which cost the always-on HUD several ms per frame. These
// helpers remember the last value set per node and skip the Godot call when it hasn't changed.
//
// Only for nodes this mod owns and styles exclusively through here: the cache assumes nothing else
// changes these overrides behind its back.
internal static class Th
{
    public static int Misses;          // debug: Godot calls actually made (value changed)
    public static string LastMiss = "";
    private static readonly ConditionalWeakTable<GodotObject, Dictionary<(char Kind, string Name), object?>> _last = new();

    private static bool Changed(GodotObject node, char kind, string name, object? value)
    {
        var d = _last.GetOrCreateValue(node);
        if (d.TryGetValue((kind, name), out var old) && Equals(old, value)) return false;
        d[(kind, name)] = value;
        if (Perf.On) { Misses++; LastMiss = $"{node.GetType().Name}.{kind}:{name}={value}"; }
        return true;
    }

    public static void Color(Control c, string name, Color v) { if (Changed(c, 'c', name, v)) c.AddThemeColorOverride(name, v); }
    public static void Const(Control c, string name, int v) { if (Changed(c, 'k', name, v)) c.AddThemeConstantOverride(name, v); }
    public static void FontSize(Control c, string name, int v) { if (Changed(c, 's', name, v)) c.AddThemeFontSizeOverride(name, v); }
    // Fonts and style boxes are compared by reference: the same resource means the same look.
    public static void Font(Control c, string name, Font f) { if (Changed(c, 'f', name, f)) c.AddThemeFontOverride(name, f); }
    public static void Stylebox(Control c, string name, StyleBox sb) { if (Changed(c, 'b', name, sb)) c.AddThemeStyleboxOverride(name, sb); }

    // Removals go through the cache too, so a later set of the same value isn't wrongly skipped.
    private static readonly object Removed = new();
    public static void RemoveColor(Control c, string name) { if (Changed(c, 'c', name, Removed)) c.RemoveThemeColorOverride(name); }
    public static void RemoveConst(Control c, string name) { if (Changed(c, 'k', name, Removed)) c.RemoveThemeConstantOverride(name); }
    public static void RemoveFont(Control c, string name) { if (Changed(c, 'f', name, Removed)) c.RemoveThemeFontOverride(name); }

    public static void Text(Label l, string s) { if (Changed(l, 't', "", s)) l.Text = s; }
    public static void Text(RichTextLabel l, string s) { if (Changed(l, 't', "", s)) l.Text = s; }

    // True when `key` differs from what was last recorded for this node under `name` (and records it).
    // For styling that builds a new resource each time (e.g. a duplicated StyleBox): only rebuild on change.
    public static bool Differs(GodotObject node, string name, object? key) => Changed(node, 'x', name, key);
}
