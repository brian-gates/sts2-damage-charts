using System;
using Godot;

namespace STS2_DamageCharts;

// Shared visual styling that matches STS2's own look (StsColors palette + game font + outlines),
// plus a resolution scale helper, so the overlay reads as native and adapts to any window size.
internal static class UiTheme
{
    // StsColors-matching palette (hex values lifted from the game's StsColors).
    public static readonly Color Cream = Html("FFF6E2");   // primary text
    public static readonly Color Gold = Html("EFC851");    // headers / highlights
    public static readonly Color Red = Html("FF5555");     // taken / incoming
    public static readonly Color Gray = new(0.75f, 0.75f, 0.75f);
    public static readonly Color Border = new(0.55f, 0.60f, 0.72f, 0.30f);
    public static readonly Color Outline = new(0f, 0f, 0f, 0.85f);

    // Per-player bar palette (player 0 ~ game blue, then orange/purple/green).
    public static readonly Color[] Players = { Html("87CEEB"), Html("FFA518"), Html("EE82EE"), Html("7FFF00") };

    private static Color Html(string hex) { try { return new Color(hex); } catch { return Colors.White; } }

    public static float Scale(Vector2 viewport)
    {
        float s = viewport.Y > 0 ? viewport.Y / 1080f : 1f;
        return Math.Clamp(s, 0.75f, 2.0f);
    }

    public static StyleBoxFlat PanelStyle(float bgAlpha, float scale)
    {
        var sb = new StyleBoxFlat { BgColor = new Color(0.04f, 0.05f, 0.08f, bgAlpha), BorderColor = Border };
        sb.SetCornerRadiusAll((int)Math.Round(6 * scale));
        sb.SetBorderWidthAll(1);
        sb.SetContentMarginAll(6 * scale);
        return sb;
    }

    public static Panel MakePanel(float bgAlpha, float scale)
    {
        var p = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        ApplyPanelStyle(p, bgAlpha, scale);
        return p;
    }

    public static void RestylePanel(Panel p, float bgAlpha, float scale)
    {
        try { ApplyPanelStyle(p, bgAlpha, scale); } catch { }
    }

    // Prefer the game's own panel frame (lifted live from its theme) so the overlay reads as native;
    // fall back to the hand-built flat style if no native stylebox was captured. Duplicate the native
    // resource so per-panel use never mutates the shared game stylebox.
    private static void ApplyPanelStyle(Panel p, float bgAlpha, float scale)
    {
        // The game's nine-patch (tiny_nine_patch) is a WHITE rounded-rect mask meant to be tinted.
        // Tint it dark via ModulateColor so our light text stays readable; floor the alpha so the
        // always-on bars panel isn't too see-through to read.
        if (_nativePanel is StyleBoxTexture nat)
        {
            try
            {
                var sb = (StyleBoxTexture)nat.Duplicate();
                sb.ModulateColor = new Color(0.04f, 0.05f, 0.08f, Math.Max(bgAlpha, 0.82f));
                p.AddThemeStyleboxOverride("panel", sb);
                return;
            }
            catch { }
        }
        p.AddThemeStyleboxOverride("panel", PanelStyle(bgAlpha, scale));
    }

    // --- Game font (lifted once from a live game label; falls back to the default font) ---
    private static Font? _font;
    private static bool _resolved;

    public static void EnsureFont(Node? sceneRoot)
    {
        if (_resolved || sceneRoot == null) return;
        try
        {
            var rtl = FindFirst<RichTextLabel>(sceneRoot);
            if (rtl != null) _font = rtl.GetThemeDefaultFont();
            if (_font == null)
            {
                var lbl = FindFirst<Label>(sceneRoot);
                if (lbl != null) _font = lbl.GetThemeDefaultFont();
            }
        }
        catch { }
        if (_font == null) { try { _font = ThemeDB.FallbackFont; } catch { } }
        if (_font != null) _resolved = true; // lock in once we have a font
    }

    // --- Native panel frame (lifted once from the game's live theme; falls back to PanelStyle) ---
    private static StyleBox? _nativePanel;
    private static bool _panelResolved;

    public static void EnsurePanelStyle(Node? sceneRoot)
    {
        if (_panelResolved || sceneRoot == null) return;
        // STS2 skins panels with NinePatchRect texture art (not theme styleboxes). Lift the game's
        // generic small info-panel frame — `tiny_nine_patch` (used by the continue-run info box) — live,
        // copying its exact 9-slice margins, and wrap it in a StyleBoxTexture for our panels. Falls back
        // to any *_nine_patch.* frame, else PanelStyle until one is on screen (captured at the menu).
        var np = FindNinePatch(sceneRoot, "tiny_nine_patch") ?? FindNinePatch(sceneRoot, "nine_patch");
        if (np?.Texture != null)
        {
            try
            {
                var sb = new StyleBoxTexture { Texture = np.Texture };
                sb.SetTextureMargin(Side.Left, np.PatchMarginLeft);
                sb.SetTextureMargin(Side.Top, np.PatchMarginTop);
                sb.SetTextureMargin(Side.Right, np.PatchMarginRight);
                sb.SetTextureMargin(Side.Bottom, np.PatchMarginBottom);
                _nativePanel = sb;
                _panelResolved = true;
                GD.Print($"[STS2 Damage] native panel captured: tex='{np.Texture.ResourcePath}' " +
                         $"margins=L{np.PatchMarginLeft} T{np.PatchMarginTop} R{np.PatchMarginRight} B{np.PatchMarginBottom}");
            }
            catch { }
        }
    }

    private static NinePatchRect? FindNinePatch(Node start, string texSubstring)
    {
        try
        {
            if (start is NinePatchRect np && np.Texture != null
                && np.Texture.ResourcePath.Contains(texSubstring)) return np;
            foreach (var c in start.GetChildren())
            {
                var r = FindNinePatch(c, texSubstring);
                if (r != null) return r;
            }
        }
        catch { }
        return null;
    }

    public static Label MakeLabel(int fontSize, Color color)
    {
        var lbl = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        Apply(lbl, fontSize, color);
        return lbl;
    }

    public static void Apply(Label lbl, int fontSize, Color color)
    {
        if (_font != null) lbl.AddThemeFontOverride("font", _font);
        lbl.AddThemeFontSizeOverride("font_size", Math.Max(1, fontSize));
        lbl.AddThemeColorOverride("font_color", color);
        lbl.AddThemeColorOverride("font_outline_color", Outline);
        lbl.AddThemeConstantOverride("outline_size", 4);
    }

    private static T? FindFirst<T>(Node start) where T : Node
    {
        try
        {
            if (start is T t) return t;
            foreach (var c in start.GetChildren())
            {
                var r = FindFirst<T>(c);
                if (r != null) return r;
            }
        }
        catch { }
        return null;
    }
}
