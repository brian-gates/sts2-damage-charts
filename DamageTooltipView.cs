using System;
using System.Collections.Generic;
using Godot;

namespace STS2_DamageCharts;

// Themed popup shown near the cursor when hovering a bar group: a round of the compact chart, a round of
// the full view's "Damage / Round" chart, or a whole fight of the recap's "Damage / Fight" chart. It draws
// a GroupDetail: a title (plus an Elite/Boss tag, encounter name and round count when known), then one
// section per player with dealt by source, taken (by source when recorded) and heal/block. Flips to the
// cursor's left/up near screen edges and stays inside the viewport; scales with resolution. With
// native_visuals it is drawn as one of the game's own hover tips (see NativeLook for why it's reproduced
// rather than instanced). The layout is rebuilt only when the detail object, scale or look changes; on
// other frames it is just repositioned.
internal sealed class DamageTooltipView
{
    // Native layout, in 1080p units: hover_tip.tscn draws text at 22px (Title bold, Description regular)
    // with a 300px minimum description width; rows run a touch smaller so a long list stays compact.
    private const float NativeContentW = 290f;
    private const float NativeContentWPct = 340f;   // amounts carry "(NN%)"
    private const float NativeIcon = 26f;
    // The flat look is the same layout drawn ~3/4 size inside a plain themed panel.
    private const float FlatUnit = 0.74f;
    private const float FlatPad = 11f;               // in layout units (x FlatUnit)

    private static readonly Color HealColor = new(0.45f, 0.85f, 0.45f);
    private static readonly Color BlockColor = new(0.50f, 0.72f, 0.96f);
    private static readonly Color EliteColor = new(1.00f, 0.66f, 0.25f);

    private readonly CanvasLayer _layer;
    public float UiScaleMul = 1f;   // config ui_scale, like the other views
    private readonly Control _container;
    private readonly Panel _bg;
    private readonly Panel _frameShadow;
    private readonly Panel _frame;
    private bool _frameStyled;
    private readonly List<Label> _labels = new();
    private readonly List<TextureRect> _icons = new();

    // Layout cache: what the current nodes show.
    private GroupDetail? _shown;
    private float _shownSc;
    private bool _shownNative;
    private Vector2 _shownVp;
    private Vector2 _size;

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
    public void Hide() { if (IsValid() && _layer.Visible) _layer.Visible = false; }
    public void Dispose() { if (IsValid()) _layer.QueueFree(); }

    public void Render(GroupDetail detail, Color[] palette, Vector2 mouseGlobal)
    {
        if (!IsValid()) return;
        try
        {
            if (!_layer.Visible) _layer.Visible = true;
            Vector2 vp;
            try { vp = _container.GetViewportRect().Size; } catch { vp = new Vector2(1920, 1080); }
            float sc = UiTheme.Scale(vp) * Math.Clamp(UiScaleMul, 0.5f, 4f);
            bool native = NativeLook.EnsureFrame(_frame, _frameShadow, ref _frameStyled);

            if (!ReferenceEquals(detail, _shown) || sc != _shownSc || native != _shownNative || vp != _shownVp)
            {
                // Show as many source rows as fit the viewport (several players can stack up), at least one.
                float maxH = vp.Y * 0.94f;
                int cur = 0, iconCur = 0;
                for (int k = Math.Max(1, detail.MaxRows); ; k--)
                {
                    cur = 0; iconCur = 0;
                    _size = Layout(detail, palette, sc, native, k, ref cur, ref iconCur);
                    if (_size.Y <= maxH || k <= 1) break;
                }
                for (int i = cur; i < _labels.Count; i++) _labels[i].Visible = false;
                for (int i = iconCur; i < _icons.Count; i++) _icons[i].Visible = false;
                _container.SetSize(_size);
                _shown = detail; _shownSc = sc; _shownNative = native; _shownVp = vp;
            }

            // Place near cursor but flip to its left/up near edges so it never lands under the cursor.
            // Offset clears the cursor graphic so the pointer never overlaps the panel.
            float W = _size.X, height = _size.Y;
            float off = 44f * sc;
            float x = mouseGlobal.X + off;
            if (x + W > vp.X) x = mouseGlobal.X - W - off;
            float py = mouseGlobal.Y + off;
            if (py + height > vp.Y) py = mouseGlobal.Y - height - off;
            x = Math.Clamp(x, 0f, Math.Max(0f, vp.X - W));
            py = Math.Clamp(py, 0f, Math.Max(0f, vp.Y - height));
            _container.SetPosition(new Vector2(x, py));
        }
        catch (Exception ex) { _shown = null; ErrorLog.Once("tooltip render", ex); }
    }

    // One layout for both looks. Native: hover_tip.png frame + shadow, gold Kreon-bold title, cream
    // Kreon-regular text with the tip's soft shadow, amounts right-aligned in the game's numeric Kreon, each
    // source's own icon, and the attack-intent sword on Taken. Flat (native_visuals off or the art missing):
    // the themed panel, the default font, the same rows at ~3/4 size.
    private Vector2 Layout(GroupDetail d, Color[] palette, float sc, bool native, int k, ref int cur, ref int iconCur)
    {
        float u = native ? sc : sc * FlatUnit;           // px per layout unit
        Font? title = native ? GameAssets.NumericFont : null;  // hover_tip.tscn Title: kreon_bold_glyph_space_one
        Font? body = native ? GameAssets.BodyFont : null;      // hover_tip.tscn Description: kreon_regular_glyph_space_one
        Font? num = native ? GameAssets.NumericFont : null;
        var dim = new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.6f);

        float contentW = (d.ShowPct ? NativeContentWPct : NativeContentW) * u;
        float left = (native ? NativeLook.InsetL : FlatPad) * u;
        float W = left + contentW + (native ? NativeLook.InsetR : FlatPad) * u;
        float right = left + contentW;
        float y = (native ? NativeLook.InsetT : FlatPad) * u;
        float rowH = 30f * u, iconSz = NativeIcon * u, gap = 8f * u;
        float amtW = (d.ShowPct ? 120f : 80f) * u;

        // Title row, with the fight's tag right-aligned in its accent color.
        string? tag = d.Kind switch { FightKind.Elite => "ELITE", FightKind.Boss => "BOSS", _ => null };
        float tagW = tag != null ? 70f * u : 0f;
        Text(ref cur, native, d.Title, F(22, u), UiTheme.Gold, title, left, y, contentW - tagW, 34f * u, HorizontalAlignment.Left);
        if (tag != null)
            Text(ref cur, native, tag, F(18, u), d.Kind == FightKind.Boss ? UiTheme.Red : EliteColor, title,
                 right - tagW, y, tagW, 34f * u, HorizontalAlignment.Right);
        y += 36f * u;
        if (!string.IsNullOrEmpty(d.Subtitle))
        {
            Text(ref cur, native, d.Subtitle!, F(18, u), UiTheme.Cream, body, left, y, contentW, 26f * u, HorizontalAlignment.Left);
            y += 26f * u;
        }
        if (!string.IsNullOrEmpty(d.Note))
        {
            Text(ref cur, native, d.Note!, F(16, u), dim, body, left, y, contentW, 24f * u, HorizontalAlignment.Left);
            y += 24f * u;
        }

        float indent = left + 4f * u;
        for (int p = 0; p < d.Players.Count; p++)
        {
            var pd = d.Players[p];
            if (p > 0) y += 8f * u;
            if (pd.Label.Length > 0)
            {
                var pc = palette.Length > 0 ? palette[Math.Clamp(pd.Slot, 0, palette.Length - 1)] : UiTheme.Cream;
                Text(ref cur, native, pd.Label, F(20, u), pc, title, left, y, contentW, rowH, HorizontalAlignment.Left);
                y += rowH;
            }

            AmountRow(ref cur, native, "Dealt", pd.DealtTotal.ToString(), UiTheme.Cream, body, num, F(20, u), left, right, y, rowH, amtW);
            y += rowH;
            y = SourceRows(ref cur, ref iconCur, native, d, pd.Dealt, false, k, body, num, dim, u, indent, right, y, rowH, iconSz, gap, amtW);

            y += 4f * u;
            float takenX = left;
            if (native && pd.TakenTotal > 0)
            {
                var sword = GameAssets.IntentAttackIcon((int)Math.Min(pd.TakenTotal, int.MaxValue));
                if (sword != null)
                {
                    IconAt(ref iconCur, sword, left, y + (rowH - iconSz) * 0.5f, iconSz);
                    takenX = left + iconSz + gap;
                }
            }
            AmountRow(ref cur, native, "Taken", pd.TakenTotal.ToString(), UiTheme.Red, body, num, F(20, u), takenX, right, y, rowH, amtW);
            y += rowH;
            y = SourceRows(ref cur, ref iconCur, native, d, pd.Taken, true, k, body, num, dim, u, indent, right, y, rowH, iconSz, gap, amtW);

            if (pd.Heal != null || pd.Block != null)
            {
                float half = contentW * 0.5f;
                if (pd.Heal is long h)
                    AmountRow(ref cur, native, "Healed", h.ToString(), HealColor, body, num, F(18, u), left, left + half - 12f * u, y, rowH, 60f * u);
                if (pd.Block is long b)
                    AmountRow(ref cur, native, "Block", b.ToString(), BlockColor, body, num, F(18, u), left + half + 12f * u, right, y, rowH, 60f * u);
                y += rowH;
            }
        }

        float height = y + (native ? NativeLook.InsetB : FlatPad) * u;
        if (native)
        {
            _bg.Visible = false;
            NativeLook.LayoutFrame(_frame, _frameShadow, W, height, sc, 1f);
        }
        else
        {
            NativeLook.HideFrame(_frame, _frameShadow);
            _bg.Visible = true;
            UiTheme.RestylePanel(_bg, 0.95f, sc);
            _bg.SetPosition(Vector2.Zero);
            _bg.SetSize(new Vector2(W, height));
        }
        return new Vector2(W, height);
    }

    // Up to k sources: icon, name (xN when a round's source hit more than once), amount (and share).
    private float SourceRows(ref int cur, ref int iconCur, bool native, GroupDetail d, List<DetailRow> rows, bool taken, int k,
                             Font? body, Font? num, Color dim, float u, float indent, float right, float y,
                             float rowH, float iconSz, float gap, float amtW)
    {
        int shown = Math.Min(rows.Count, k);
        for (int i = 0; i < shown; i++)
        {
            var r = rows[i];
            float textX = indent;
            if (native)
            {
                var tex = NativeLook.Icon(r.Icon);
                if (tex == null && taken && r.Name is not ("Status" or "Other" or "Unknown"))
                    tex = GameAssets.IntentAttackIcon((int)Math.Min(r.Amount, int.MaxValue));
                if (tex != null)
                {
                    IconAt(ref iconCur, tex, indent, y + (rowH - iconSz) * 0.5f, iconSz);
                    textX = indent + iconSz + gap;
                }
            }
            string nm = r.Count > 1 ? $"{r.Name} x{r.Count}" : r.Name;
            Text(ref cur, native, nm, F(18, u), UiTheme.Cream, body, textX, y, Math.Max(1f, right - textX - amtW), rowH, HorizontalAlignment.Left);
            string amt = d.ShowPct ? $"{r.Amount} ({r.Pct}%)" : r.Amount.ToString();
            Text(ref cur, native, amt, F(18, u), UiTheme.Cream, num, right - amtW, y, amtW, rowH, HorizontalAlignment.Right);
            y += rowH;
        }
        if (rows.Count > shown)
        {
            Text(ref cur, native, $"+ {rows.Count - shown} more", F(16, u), dim, body, indent, y, right - indent, 24f * u, HorizontalAlignment.Left);
            y += 24f * u;
        }
        return y;
    }

    // "Label ........ 123" on one row: label in the body font, amount right-aligned in the numeric font.
    private void AmountRow(ref int cur, bool native, string label, string amount, Color color, Font? body, Font? num, int size,
                           float x, float right, float y, float rowH, float amtW)
    {
        Text(ref cur, native, label, size, color, body, x, y, Math.Max(1f, right - x - amtW), rowH, HorizontalAlignment.Left);
        Text(ref cur, native, amount, size, color, num, right - amtW, y, amtW, rowH, HorizontalAlignment.Right);
    }

    private void Text(ref int cur, bool native, string text, int size, Color color, Font? font,
                      float x, float y, float w, float h, HorizontalAlignment align)
    {
        var l = Take(ref cur, size, color);
        if (native) NativeLook.Style(l, size, color, font, 0);
        else { NativeLook.Reset(l); UiTheme.Apply(l, size, color); }
        l.Text = text;
        Place(l, x, y, w, h, align);
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

    private static int F(int b, float unit) => Math.Max(9, (int)Math.Round(b * unit));

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
}
