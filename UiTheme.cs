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

    // --- TEMP diagnostic: discover the game's real panel art (themes + nine-patch nodes) ---
    // Returns true once it found a populated UI tree (so callers can stop retrying).
    public static bool DumpThemeStyleboxes(Node? sceneRoot)
    {
        if (sceneRoot == null) return false;
        try
        {
            // Sanity: how populated is the tree, and what's up top?
            int controlCount = 0, ninePatchCount = 0, textureRectCount = 0;
            CountNodes(sceneRoot, ref controlCount, ref ninePatchCount, ref textureRectCount, 0);
            GD.Print($"[STS2 Damage] theme: tree controls={controlCount} ninepatch={ninePatchCount} textureRects={textureRectCount}");
            if (controlCount == 0) return false; // UI not built yet — retry later
            DumpInputMap();
            // Coordinate space: viewport size vs OS window vs content scale (explains tooltip offset).
            try
            {
                var root = ((SceneTree)Engine.GetMainLoop()).Root;
                var win = DisplayServer.WindowGetSize();
                GD.Print($"[STS2 Damage] theme: viewport={root.GetVisibleRect().Size} window={win} " +
                         $"contentScaleFactor={root.ContentScaleFactor} contentScaleSize={root.ContentScaleSize}");
            }
            catch (Exception e) { GD.Print($"[STS2 Damage] theme: viewport probe failed: {e.Message}"); }

            // Collect every distinct Theme applied anywhere in the live tree, plus the project theme.
            var themes = new System.Collections.Generic.HashSet<Theme>();
            try { var pt = ThemeDB.GetProjectTheme(); if (pt != null) themes.Add(pt); } catch { }
            CollectThemes(sceneRoot, themes, 0);
            GD.Print($"[STS2 Damage] theme: found {themes.Count} distinct Theme resource(s)");

            int idx = 0;
            foreach (var th in themes)
            {
                idx++;
                try
                {
                    foreach (var type in th.GetStyleboxTypeList())
                    {
                        foreach (var name in th.GetStyleboxList(type))
                        {
                            try
                            {
                                var sb = th.GetStylebox(name, type);
                                string cls = sb?.GetType().Name ?? "null";
                                if (sb is StyleBoxTexture tex)
                                {
                                    GD.Print($"[STS2 Damage] theme[{idx}] TEXTURE type='{type}' name='{name}' " +
                                             $"tex='{tex.Texture?.ResourcePath}' " +
                                             $"texMargins=L{tex.GetTextureMargin(Side.Left)} T{tex.GetTextureMargin(Side.Top)} " +
                                             $"R{tex.GetTextureMargin(Side.Right)} B{tex.GetTextureMargin(Side.Bottom)} res='{th.ResourcePath}'");
                                }
                                else
                                {
                                    GD.Print($"[STS2 Damage] theme[{idx}] {cls} type='{type}' name='{name}'");
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            // Per-control resolution: a live Label/RichTextLabel may resolve panel styleboxes the
            // theme-resource scan misses (game applies styles via inherited/default theme chains).
            var probe = (Control?)FindFirst<RichTextLabel>(sceneRoot) ?? FindFirst<Label>(sceneRoot);
            if (probe != null)
            {
                foreach (var (name, type) in new[] { ("panel", "TooltipPanel"), ("panel", "PanelContainer"),
                    ("panel", "Panel"), ("panel", "PopupPanel"), ("panel", "PopupMenu") })
                {
                    try
                    {
                        bool has = probe.HasThemeStylebox(name, type);
                        var sb = has ? probe.GetThemeStylebox(name, type) : null;
                        GD.Print($"[STS2 Damage] probe stylebox name='{name}' type='{type}' has={has} cls={(sb?.GetType().Name ?? "-")}" +
                                 (sb is StyleBoxTexture t ? $" tex='{t.Texture?.ResourcePath}'" : ""));
                    }
                    catch { }
                }
            }

            // Nine-patch & texture rects are how Godot UIs usually apply textured frames/panels.
            int tc = 0;
            DumpTextureNodes(sceneRoot, ref tc);
            GD.Print($"[STS2 Damage] theme: dumped {tc} texture/ninepatch node(s)");
            return true;
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] theme dump failed: {ex.Message}"); }
        return false;
    }

    private static void CountNodes(Node n, ref int controls, ref int ninepatch, ref int texrect, int depth)
    {
        if (depth > 80) return;
        try
        {
            if (n is Control) controls++;
            if (n is NinePatchRect) ninepatch++;
            if (n is TextureRect) texrect++;
            foreach (var ch in n.GetChildren()) CountNodes(ch, ref controls, ref ninepatch, ref texrect, depth + 1);
        }
        catch { }
    }

    // Continuous, deduped dump of every texture-bearing UI node (NinePatchRect/TextureRect) and any
    // Panel with a StyleBoxTexture. Logs each distinct texture path once, with its ancestor chain, so
    // contextual UI (settings panels, popups, hover-tips, banners) reveals the game's real frame art.
    private static readonly System.Collections.Generic.HashSet<string> _loggedTex = new();

    // TEMP diagnostic: list every Godot InputMap action and its bound keys (reveals which keys the
    // game binds). NOTE: some game hotkeys are handled directly in NHotkeyManager, not via InputMap.
    public static void DumpInputMap()
    {
        try
        {
            foreach (var action in InputMap.GetActions())
            {
                var parts = new System.Collections.Generic.List<string>();
                foreach (var e in InputMap.ActionGetEvents(action))
                {
                    if (e is InputEventKey k)
                    {
                        var key = k.PhysicalKeycode != Key.None ? k.PhysicalKeycode : k.Keycode;
                        string mods = (k.CtrlPressed ? "Ctrl+" : "") + (k.MetaPressed ? "Cmd+" : "")
                                    + (k.AltPressed ? "Alt+" : "") + (k.ShiftPressed ? "Shift+" : "");
                        parts.Add(mods + key);
                    }
                    else if (e is InputEventMouseButton mb) parts.Add("Mouse" + mb.ButtonIndex);
                    else if (e is InputEventJoypadButton jb) parts.Add("Joy" + jb.ButtonIndex);
                    else parts.Add(e.GetType().Name);
                }
                GD.Print($"[STS2 Damage] action '{action}' = [{string.Join(", ", parts)}]");
            }
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] inputmap dump failed: {ex.Message}"); }
    }

    public static void DumpNewTextures(Node? root)
    {
        if (root == null) return;
        try { int n = 0; DumpTextureNodes(root, ref n); } catch { }
    }

    private static void DumpTextureNodes(Node n, ref int count)
    {
        try
        {
            string? path = null; string kind = "";
            if (n is NinePatchRect np && np.Texture != null) { path = np.Texture.ResourcePath; kind = "ninepatch"; }
            else if (n is TextureRect tr && tr.Texture != null) { path = tr.Texture.ResourcePath; kind = "texrect"; }
            else if (n is Panel pn)
            {
                try { if ((pn.HasThemeStylebox("panel") ? pn.GetThemeStylebox("panel") : null) is StyleBoxTexture st)
                    { path = st.Texture?.ResourcePath; kind = "paneltex"; } }
                catch { }
            }
            if (!string.IsNullOrEmpty(path) && _loggedTex.Add(path!) && count < 200)
            {
                count++;
                GD.Print($"[STS2 Damage] {kind} tex='{path}' node='{n.Name}' chain='{Ancestry(n)}'");
            }
            foreach (var ch in n.GetChildren()) DumpTextureNodes(ch, ref count);
        }
        catch { }
    }

    private static string Ancestry(Node n)
    {
        var parts = new System.Collections.Generic.List<string>();
        try { var p = n.GetParent(); int d = 0; while (p != null && d < 6) { parts.Add(p.GetType().Name); p = p.GetParent(); d++; } }
        catch { }
        return string.Join("<", parts);
    }

    private static void CollectThemes(Node n, System.Collections.Generic.HashSet<Theme> acc, int depth)
    {
        if (depth > 60) return;
        try
        {
            if (n is Control c && c.Theme != null) acc.Add(c.Theme);
            if (n is Window w && w.Theme != null) acc.Add(w.Theme);
            foreach (var ch in n.GetChildren()) CollectThemes(ch, acc, depth + 1);
        }
        catch { }
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

    private static Node? FindFirstByTypeName(Node start, string typeName)
    {
        try
        {
            if (start.GetType().Name == typeName) return start;
            foreach (var c in start.GetChildren())
            {
                var r = FindFirstByTypeName(c, typeName);
                if (r != null) return r;
            }
        }
        catch { }
        return null;
    }
}
