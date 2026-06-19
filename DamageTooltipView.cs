using System;
using System.Collections.Generic;
using Godot;

namespace STS2_DamageCharts;

// Small themed popup shown near the cursor when hovering a round's bar: that round's dealt-by-source
// (icon + name + xN + amount) plus the round's taken total. Flips to the cursor's left/up near screen
// edges so it never sits under the cursor; scales with resolution.
internal sealed class DamageTooltipView
{
    private const float BaseWidth = 250f;
    private const int MaxRows = 8;

    private readonly CanvasLayer _layer;
    private readonly Control _container;
    private readonly Panel _bg;
    private readonly List<Label> _labels = new();
    private readonly List<TextureRect> _icons = new();
    private readonly Dictionary<string, Texture2D?> _texCache = new();

    public DamageTooltipView(Node root)
    {
        _layer = new CanvasLayer { Layer = 131, Name = "Sts2DamageChartsTooltip", Visible = false };
        root.AddChild(_layer);

        _container = new Control { Name = "Tooltip", MouseFilter = Control.MouseFilterEnum.Ignore };
        _layer.AddChild(_container);

        _bg = UiTheme.MakePanel(0.95f, 1f);
        _bg.SetPosition(Vector2.Zero);
        _container.AddChild(_bg);
    }

    public bool IsValid() => GodotObject.IsInstanceValid(_layer);
    public void Hide() { if (IsValid()) _layer.Visible = false; }
    public void Dispose() { if (IsValid()) _layer.QueueFree(); }

    public void Render(HoverInfo info, Vector2 mouseGlobal)
    {
        if (!IsValid()) return;
        try
        {
            _layer.Visible = true;
            Vector2 vp;
            try { vp = _container.GetViewportRect().Size; } catch { vp = new Vector2(1920, 1080); }
            float sc = UiTheme.Scale(vp);
            float W = BaseWidth * sc;
            float rowH = 20f * sc, pad = 8f * sc;
            UiTheme.RestylePanel(_bg, 0.95f, sc);

            int cur = 0, iconCur = 0;
            float y = pad;

            string head = info.PlayerLabel.Length > 0 ? $"Round {info.Round} — {info.PlayerLabel}" : $"Round {info.Round}";
            var h = Next(ref cur, F(15, sc), UiTheme.Gold);
            h.Text = head;
            h.SetPosition(new Vector2(pad, y));
            y += 24f * sc;

            var dlt = Next(ref cur, F(13, sc), UiTheme.Cream);
            dlt.Text = $"Dealt {info.DealtTotal}";
            dlt.SetPosition(new Vector2(pad, y));
            y += rowH;

            float iconSz = 17f * sc;
            int shown = Math.Min(info.Dealt.Count, MaxRows);
            for (int i = 0; i < shown; i++)
            {
                var d = info.Dealt[i];
                float textX = pad + 6f * sc;
                if (d.Icon != null)
                {
                    var tex = LoadTex(d.Icon);
                    if (tex != null)
                    {
                        var ic = NextIcon(ref iconCur);
                        ic.Texture = tex;
                        ic.SetSize(new Vector2(iconSz, iconSz));
                        ic.SetPosition(new Vector2(pad + 6f * sc, y));
                        textX = pad + 6f * sc + iconSz + 4f * sc;
                    }
                }
                var row = Next(ref cur, F(13, sc), new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.92f));
                string nm = d.Count > 1 ? $"{d.Label} x{d.Count}" : d.Label;
                row.Text = $"{Truncate(nm, 22)}  {d.Sum}";
                row.SetPosition(new Vector2(textX, y));
                y += rowH;
            }
            if (info.Dealt.Count > shown)
            {
                var more = Next(ref cur, F(11, sc), new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.5f));
                more.Text = $"+ {info.Dealt.Count - shown} more";
                more.SetPosition(new Vector2(pad + 6f * sc, y));
                y += rowH;
            }

            var tk = Next(ref cur, F(13, sc), UiTheme.Red);
            tk.Text = $"Taken {info.TakenTotal}";
            tk.SetPosition(new Vector2(pad, y));
            y += 22f * sc;

            float height = y + pad;
            _container.SetSize(new Vector2(W, height));
            _bg.SetSize(new Vector2(W, height));

            // Place near cursor but flip to its left/up near edges so it never lands under the cursor.
            // Offset clears the cursor graphic so the pointer never overlaps the panel.
            float off = 44f * sc;
            float x = mouseGlobal.X + off;
            if (x + W > vp.X) x = mouseGlobal.X - W - off;
            float py = mouseGlobal.Y + off;
            if (py + height > vp.Y) py = mouseGlobal.Y - height - off;
            x = Math.Clamp(x, 0f, Math.Max(0f, vp.X - W));
            py = Math.Clamp(py, 0f, Math.Max(0f, vp.Y - height));
            _container.SetPosition(new Vector2(x, py));

            for (int i = cur; i < _labels.Count; i++) _labels[i].Visible = false;
            for (int i = iconCur; i < _icons.Count; i++) _icons[i].Visible = false;
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] tooltip render error: {ex.Message}"); }
    }

    private static int F(int b, float sc) => Math.Max(9, (int)Math.Round(b * sc));
    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private Label Next(ref int cursor, int fontSize, Color color)
    {
        Label lbl;
        if (cursor < _labels.Count) lbl = _labels[cursor];
        else { lbl = UiTheme.MakeLabel(fontSize, color); _container.AddChild(lbl); _labels.Add(lbl); }
        UiTheme.Apply(lbl, fontSize, color);
        lbl.Visible = true;
        cursor++;
        return lbl;
    }

    private TextureRect NextIcon(ref int cursor)
    {
        TextureRect ic;
        if (cursor < _icons.Count) ic = _icons[cursor];
        else
        {
            ic = new TextureRect
            {
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            };
            _container.AddChild(ic);
            _icons.Add(ic);
        }
        ic.Visible = true;
        cursor++;
        return ic;
    }

    private Texture2D? LoadTex(string path)
    {
        if (_texCache.TryGetValue(path, out var cached)) return cached;
        Texture2D? tex = null;
        try { if (ResourceLoader.Exists(path)) tex = ResourceLoader.Load<Texture2D>(path, null, ResourceLoader.CacheMode.Reuse); }
        catch { tex = null; }
        _texCache[path] = tex;
        return tex;
    }
}
