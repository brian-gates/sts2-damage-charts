using System;
using System.Collections.Generic;
using Godot;

namespace STS2_DamageCharts;

// Small themed popup shown near the cursor when hovering a round's bar: that round's dealt-by-source
// (icon + name + xN + amount) plus the round's taken total. Flips to the cursor's left/up near screen
// edges so it never sits under the cursor; scales with resolution. With native_visuals it is drawn as one
// of the game's own hover tips (see NativeLook for why it's reproduced rather than instanced).
internal sealed class DamageTooltipView
{
    private const float BaseWidth = 250f;
    private const int MaxRows = 8;
    // Native layout, in 1080p units: hover_tip.tscn draws text at 22px (Title bold, Description regular)
    // with a 300px minimum description width; rows run a touch smaller so eight of them stay compact.
    private const float NativeContentW = 290f;
    private const float NativeIcon = 26f;

    private readonly CanvasLayer _layer;
    private readonly Control _container;
    private readonly Panel _bg;
    private readonly Panel _frameShadow;
    private readonly Panel _frame;
    private bool _frameStyled;
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

        _frameShadow = NativeLook.MakeFramePanel();
        _container.AddChild(_frameShadow);
        _frame = NativeLook.MakeFramePanel();
        _container.AddChild(_frame);
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
            int cur = 0, iconCur = 0;
            Vector2 size = NativeLook.EnsureFrame(_frame, _frameShadow, ref _frameStyled)
                ? RenderNative(info, sc, ref cur, ref iconCur)
                : RenderFlat(info, sc, ref cur, ref iconCur);
            float W = size.X, height = size.Y;
            _container.SetSize(size);

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

    // The original flat look (native_visuals off, or the game's hover-tip art unavailable).
    private Vector2 RenderFlat(HoverInfo info, float sc, ref int cur, ref int iconCur)
    {
        _bg.Visible = true;
        NativeLook.HideFrame(_frame, _frameShadow);
        float W = BaseWidth * sc;
        float rowH = 20f * sc, pad = 8f * sc;
        UiTheme.RestylePanel(_bg, 0.95f, sc);

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
        _bg.SetSize(new Vector2(W, height));
        return new Vector2(W, height);
    }

    // The game's hover-tip look: hover_tip.png frame + shadow, gold Kreon-bold title, cream Kreon-regular
    // text with the tip's soft shadow, amounts right-aligned in the game's numeric Kreon, and each source's
    // own icon (power/relic/potion sprite or card portrait). Taken gets the game's attack-intent sword sized
    // by the round's total, like an enemy's intent.
    private Vector2 RenderNative(HoverInfo info, float sc, ref int cur, ref int iconCur)
    {
        _bg.Visible = false;
        var title = GameAssets.NumericFont;  // hover_tip.tscn Title: kreon_bold_glyph_space_one
        var body = GameAssets.BodyFont;      // hover_tip.tscn Description: kreon_regular_glyph_space_one
        var num = GameAssets.NumericFont;
        var dim = new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.6f);

        float W = (NativeLook.InsetL + NativeContentW + NativeLook.InsetR) * sc;
        float left = NativeLook.InsetL * sc, right = W - NativeLook.InsetR * sc, contentW = right - left;
        float y = NativeLook.InsetT * sc;
        float rowH = 30f * sc, iconSz = NativeIcon * sc, gap = 8f * sc;

        string head = info.PlayerLabel.Length > 0 ? $"Round {info.Round} — {info.PlayerLabel}" : $"Round {info.Round}";
        var h = NextNative(ref cur, F(22, sc), UiTheme.Gold, title);
        h.Text = head;
        Place(h, left, y, contentW, 34f * sc, HorizontalAlignment.Left);
        y += 36f * sc;

        float amtW = 80f * sc;
        AmountRow(ref cur, "Dealt", info.DealtTotal, UiTheme.Cream, body, num, F(20, sc), left, right, y, rowH, amtW);
        y += rowH;

        int shown = Math.Min(info.Dealt.Count, MaxRows);
        float indent = left + 4f * sc;
        for (int i = 0; i < shown; i++)
        {
            var d = info.Dealt[i];
            float textX = indent;
            var tex = NativeLook.Icon(d.Icon);
            if (tex != null)
            {
                IconAt(ref iconCur, tex, indent, y + (rowH - iconSz) * 0.5f, iconSz);
                textX = indent + iconSz + gap;
            }
            string nm = d.Count > 1 ? $"{d.Label} x{d.Count}" : d.Label;
            var name = NextNative(ref cur, F(18, sc), UiTheme.Cream, body);
            name.Text = Truncate(nm, 24);
            Place(name, textX, y, Math.Max(1f, right - textX - 56f * sc), rowH, HorizontalAlignment.Left);
            var amt = NextNative(ref cur, F(18, sc), UiTheme.Cream, num);
            amt.Text = d.Sum.ToString();
            Place(amt, right - 56f * sc, y, 56f * sc, rowH, HorizontalAlignment.Right);
            y += rowH;
        }
        if (info.Dealt.Count > shown)
        {
            var more = NextNative(ref cur, F(16, sc), dim, body);
            more.Text = $"+ {info.Dealt.Count - shown} more";
            Place(more, indent, y, contentW, 24f * sc, HorizontalAlignment.Left);
            y += 24f * sc;
        }

        y += 4f * sc;
        float takenX = left;
        if (info.TakenTotal > 0)
        {
            var sword = GameAssets.IntentAttackIcon((int)Math.Min(info.TakenTotal, int.MaxValue));
            if (sword != null)
            {
                IconAt(ref iconCur, sword, left, y + (rowH - iconSz) * 0.5f, iconSz);
                takenX = left + iconSz + gap;
            }
        }
        AmountRow(ref cur, "Taken", info.TakenTotal, UiTheme.Red, body, num, F(20, sc), takenX, right, y, rowH, amtW);
        y += rowH;

        float height = y + NativeLook.InsetB * sc;
        NativeLook.LayoutFrame(_frame, _frameShadow, W, height, sc, 1f);
        return new Vector2(W, height);
    }

    // "Label ........ 123" on one row: label in the body font, amount right-aligned in the numeric font.
    private void AmountRow(ref int cur, string label, long amount, Color color, Font? body, Font? num, int size,
                           float x, float right, float y, float rowH, float amtW)
    {
        var l = NextNative(ref cur, size, color, body);
        l.Text = label;
        Place(l, x, y, Math.Max(1f, right - x - amtW), rowH, HorizontalAlignment.Left);
        var a = NextNative(ref cur, size, color, num);
        a.Text = amount.ToString();
        Place(a, right - amtW, y, amtW, rowH, HorizontalAlignment.Right);
    }

    private static void Place(Label l, float x, float y, float w, float h, HorizontalAlignment align)
    {
        l.HorizontalAlignment = align;
        l.VerticalAlignment = VerticalAlignment.Center;
        // Text columns end in an ellipsis instead of running under the amount column or past the frame;
        // amounts never trim. (Trimming also lets the label shrink below its text width to fit the slot.)
        l.TextOverrunBehavior = align == HorizontalAlignment.Left
            ? TextServer.OverrunBehavior.TrimEllipsis : TextServer.OverrunBehavior.NoTrimming;
        l.SetPosition(new Vector2(x, y));
        l.Size = Vector2.Zero; // re-fit, then widen to the slot (never narrower than the text)
        l.SetSize(new Vector2(w, h));
    }

    private void IconAt(ref int iconCur, Texture2D tex, float x, float y, float size)
    {
        var ic = NextIcon(ref iconCur);
        ic.Texture = tex;
        ic.SetSize(new Vector2(size, size));
        ic.SetPosition(new Vector2(x, y));
    }

    private Label NextNative(ref int cursor, int fontSize, Color color, Font? font)
    {
        var lbl = Take(ref cursor, fontSize, color);
        NativeLook.Style(lbl, fontSize, color, font, 0);
        return lbl;
    }

    private static int F(int b, float sc) => Math.Max(9, (int)Math.Round(b * sc));
    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private Label Next(ref int cursor, int fontSize, Color color)
    {
        var lbl = Take(ref cursor, fontSize, color);
        NativeLook.Reset(lbl); // the flat look gets an untouched label
        UiTheme.Apply(lbl, fontSize, color);
        return lbl;
    }

    private Label Take(ref int cursor, int fontSize, Color color)
    {
        Label lbl;
        if (cursor < _labels.Count) lbl = _labels[cursor];
        else { lbl = UiTheme.MakeLabel(fontSize, color); _container.AddChild(lbl); _labels.Add(lbl); }
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
