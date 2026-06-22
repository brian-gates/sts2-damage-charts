using System;
using System.Collections.Generic;
using Godot;

namespace STS2_DamageCharts;

internal sealed class HoverInfo
{
    public int Round;
    public string PlayerLabel = "";
    public List<(string Label, long Sum, int Count, string? Icon)> Dealt = new();
    public long DealtTotal;
    public long TakenTotal;
}

// Compact, always-on, draggable, resolution-scaled "Damage / Round" chart. Dealt bars stack one
// segment per source (repeats combined → "xN", icon-badged); taken is a single red bar. Native nodes
// only; poll-based mouse (UpdateMouse / TryGetHover from the tick). Position persists as a viewport
// fraction so it stays put across resolutions.
internal sealed class DamageChartView
{
    private const float BaseWidth = 340f;
    private const float BaseHeight = 168f;
    private const float BaseMargin = 14f;
    private const float BaseHeaderH = 24f;
    private const int MaxGroups = 6;
    private const float BaseMaxIcon = 34f;

    private readonly CanvasLayer _layer;
    private readonly Control _container;
    private readonly Panel _bg;
    private readonly Control _gridLayer;
    private readonly Control _barLayer;
    private readonly Control _iconLayer;
    private readonly Label _title;
    private readonly List<ColorRect> _bars = new();
    private readonly List<ColorRect> _grid = new();
    private readonly List<Label> _labels = new();
    private readonly List<TextureRect> _icons = new();
    private readonly Dictionary<string, Texture2D?> _texCache = new();

    private readonly List<(Rect2 Rect, int Round, int Slot)> _cellRects = new();
    private ChartSnapshot? _lastSnap;

    // User multiplier from config (ui_scale): enlarges the whole widget (panel + text) on top of the
    // automatic resolution scale, for legibility.
    public float UiScaleMul = 1f;

    private Vector2 _posFrac;     // top-left as fraction of viewport
    private bool _posFracInit;
    private bool _dragging;
    private bool _leftLast;
    private Vector2 _dragOffset;  // cursor − absolute pos at drag start
    private Vector2 _pressStart;
    private bool _pressedOverPanel;
    private bool _clicked;

    // cached each render for UpdateMouse/hover
    private Vector2 _vp = new(1920, 1080);
    private float _w = BaseWidth, _h = BaseHeight, _headerH = BaseHeaderH;
    private float _topInset;

    public DamageChartView(Node root, Vector2? savedPosFrac)
    {
        _layer = new CanvasLayer { Layer = 128, Name = "Sts2DamageChartsBars" };
        root.AddChild(_layer);

        _container = new Control { Name = "Bars", MouseFilter = Control.MouseFilterEnum.Ignore };
        _layer.AddChild(_container);

        _bg = UiTheme.MakePanel(0.5f, 1f);
        _bg.MouseFilter = Control.MouseFilterEnum.Stop; // block click-through over the panel
        _bg.SetPosition(Vector2.Zero);
        _container.AddChild(_bg);

        // Gridlines sit just above the bg and below the bars.
        _gridLayer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _gridLayer.SetPosition(Vector2.Zero);
        _container.AddChild(_gridLayer);

        // Dedicated layers so icons always paint above bars regardless of pool growth order:
        // bg < grid < bars < icons < labels (labels are added to _container later, i.e. on top).
        _barLayer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _barLayer.SetPosition(Vector2.Zero);
        _container.AddChild(_barLayer);
        _iconLayer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _iconLayer.SetPosition(Vector2.Zero);
        _container.AddChild(_iconLayer);

        _title = UiTheme.MakeLabel(11, new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.85f));
        _title.Text = "Damage / Round";
        _container.AddChild(_title);

        if (savedPosFrac.HasValue) { _posFrac = savedPosFrac.Value; _posFracInit = true; }
    }

    public bool IsValid() => GodotObject.IsInstanceValid(_layer);
    public void Show() { if (IsValid()) _layer.Visible = true; }
    public void Hide() { if (IsValid()) _layer.Visible = false; }
    public void Dispose() { if (IsValid()) _layer.QueueFree(); }
    public Vector2 PositionFraction => _posFrac;
    public bool IsDragging => _dragging;
    public bool TakeClick() { var c = _clicked; _clicked = false; return c; }

    public bool UpdateMouse(bool leftDown)
    {
        if (!IsValid()) return false;
        bool savedNow = false;
        try
        {
            Vector2 local = _container.GetLocalMousePosition();
            Vector2 global = _container.GetGlobalMousePosition();
            bool overPanel = local.X >= 0 && local.X <= _w && local.Y >= 0 && local.Y <= _h;
            bool overHeader = overPanel && local.Y <= _headerH;

            if (leftDown && !_leftLast) // press
            {
                _pressStart = global;
                _pressedOverPanel = overPanel;
                if (overHeader) { _dragging = true; _dragOffset = global - new Vector2(_posFrac.X * _vp.X, _posFrac.Y * _vp.Y); }
            }
            else if (leftDown && _dragging) // dragging the header
            {
                Vector2 abs = global - _dragOffset;
                float yMin = _vp.Y > 0 ? _topInset / _vp.Y : 0f;
                float x = _vp.X > 0 ? Math.Clamp(abs.X / _vp.X, 0f, Math.Max(0f, 1f - _w / _vp.X)) : 0f;
                float y = _vp.Y > 0 ? Math.Clamp(abs.Y / _vp.Y, yMin, Math.Max(yMin, 1f - _h / _vp.Y)) : 0f;
                _posFrac = new Vector2(x, y);
            }
            else if (!leftDown && _leftLast) // release
            {
                bool moved = (global - _pressStart).Length() > 5f;
                if (_dragging) { _dragging = false; if (moved) savedNow = true; else if (_pressedOverPanel) _clicked = true; }
                else if (_pressedOverPanel && !moved) _clicked = true; // click on the panel → toggle details
                _pressedOverPanel = false;
            }
            _leftLast = leftDown;
        }
        catch { }
        return savedNow;
    }

    public bool TryGetHover(out HoverInfo info)
    {
        info = null!;
        if (!IsValid() || _dragging || _lastSnap == null) return false;
        try
        {
            Vector2 local = _container.GetLocalMousePosition();
            foreach (var c in _cellRects)
            {
                if (!c.Rect.HasPoint(local)) continue;
                var built = BuildHover(c.Round, c.Slot);
                if (built == null) return false;
                info = built;
                return true;
            }
        }
        catch { }
        return false;
    }

    private HoverInfo? BuildHover(int round, int slot)
    {
        var snap = _lastSnap;
        if (snap == null) return null;
        foreach (var row in snap.Rows)
        {
            if (row.Round != round) continue;
            var info = new HoverInfo
            {
                Round = round,
                PlayerLabel = (snap.PlayerCount > 1 && slot < snap.Labels.Length) ? snap.Labels[slot] : "",
                Dealt = AggregateBySource(row.DealtSegs[slot]),
                TakenTotal = row.Taken[slot],
            };
            foreach (var d in info.Dealt) info.DealtTotal += d.Sum;
            return info;
        }
        return null;
    }

    // Y floor (viewport px) that keeps the chart below the game's top bar instead of sliding under the
    // higher-layer HUD. The bar is ~8% of viewport height (letterbox-excluded); 9% leaves a hair below.
    private static float TopInset(Vector2 vp) => Math.Max(48f, vp.Y * 0.09f);

    public void Render(ChartSnapshot snap, Color[] palette)
    {
        if (!IsValid()) return;
        try
        {
            _layer.Visible = true;
            _lastSnap = snap;
            _cellRects.Clear();

            _vp = _container.GetViewportRect().Size;
            float sc = UiTheme.Scale(_vp) * Math.Clamp(UiScaleMul, 0.5f, 4f);
            _w = BaseWidth * sc; _h = BaseHeight * sc; _headerH = BaseHeaderH * sc;
            float margin = BaseMargin * sc;

            _container.SetSize(new Vector2(_w, _h));
            _bg.SetSize(new Vector2(_w, _h));
            _gridLayer.SetSize(new Vector2(_w, _h));
            _barLayer.SetSize(new Vector2(_w, _h));
            _iconLayer.SetSize(new Vector2(_w, _h));
            UiTheme.RestylePanel(_bg, 0.5f, sc);

            float inset = TopInset(_vp);
            _topInset = inset;
            if (!_posFracInit)
            {
                _posFrac = new Vector2(_vp.X > 0 ? (_vp.X - _w - margin) / _vp.X : 0.7f, _vp.Y > 0 ? Math.Max(margin, inset) / _vp.Y : 0.02f);
                _posFracInit = true;
            }
            float px = Math.Clamp(_posFrac.X * _vp.X, 0f, Math.Max(0f, _vp.X - _w));
            float py = Math.Clamp(_posFrac.Y * _vp.Y, inset, Math.Max(inset, _vp.Y - _h));
            _container.SetPosition(new Vector2(px, py));

            UiTheme.Apply(_title, F(14, sc), new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.85f));
            _title.SetPosition(new Vector2(8 * sc, 4 * sc));

            int labelCur = 0, barCur = 0, iconCur = 0, gridCur = 0;
            float maxIcon = BaseMaxIcon * sc;

            float plotTop = 26 * sc;
            if (snap.PlayerCount > 1)
            {
                float lx = 8 * sc;
                for (int s = 0; s < snap.PlayerCount && s < palette.Length; s++)
                {
                    var lbl = NextLabel(ref labelCur, F(11, sc), palette[s]);
                    lbl.Text = "■ " + (s < snap.Labels.Length ? snap.Labels[s] : $"P{s + 1}");
                    lbl.SetPosition(new Vector2(lx, 20 * sc));
                    lx += Math.Min(100 * sc, lbl.Text.Length * 6f * sc + 14 * sc);
                }
                plotTop = 40 * sc;
            }

            float plotLeft = 30 * sc, plotRight = _w - 8 * sc, plotBottom = _h - 18 * sc; // left gutter for Y-axis values
            float plotW = plotRight - plotLeft, plotH = plotBottom - plotTop;

            int total = snap.Rows.Count;
            if (total == 0)
            {
                // Nothing to show yet — hide the whole widget rather than render an empty box.
                HideRest(_labels, 0); HideRest(_bars, 0); HideRest(_icons, 0); HideRest(_grid, 0);
                _layer.Visible = false;
                return;
            }

            int start = Math.Max(0, total - MaxGroups);
            int groups = total - start;

            int maxVal = 1;
            for (int i = start; i < total; i++)
                for (int s = 0; s < snap.PlayerCount; s++)
                {
                    if (snap.Rows[i].Dealt[s] > maxVal) maxVal = snap.Rows[i].Dealt[s];
                    if (snap.Rows[i].Taken[s] > maxVal) maxVal = snap.Rows[i].Taken[s];
                }

            // Y-axis: faint gridlines at 0 / mid / peak with value labels in the left gutter.
            var axisColor = new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.55f);
            for (int g = 0; g <= 2; g++)
            {
                float frac = g * 0.5f;
                float yLine = plotBottom - frac * plotH;
                var gl = NextGrid(ref gridCur);
                gl.Color = new Color(1f, 1f, 1f, g == 0 ? 0.22f : 0.10f);
                gl.SetPosition(new Vector2(plotLeft, yLine));
                gl.SetSize(new Vector2(plotW, Math.Max(1f, sc)));
                var vl = NextLabel(ref labelCur, F(13, sc), axisColor);
                vl.Text = ((long)Math.Round(frac * maxVal)).ToString();
                vl.SetPosition(new Vector2(3 * sc, yLine - 7 * sc));
            }

            // Constant bar width regardless of round count: size each round group to the chart's capacity
            // (MaxGroups), left-aligned, so a single early round isn't stretched across the whole plot.
            float groupW = plotW / MaxGroups;
            float cellW = groupW / snap.PlayerCount;

            for (int gi = 0; gi < groups; gi++)
            {
                var row = snap.Rows[start + gi];
                float groupX = plotLeft + gi * groupW;

                for (int s = 0; s < snap.PlayerCount; s++)
                {
                    var c = palette[Math.Min(s, palette.Length - 1)];
                    float cellX = groupX + s * cellW;
                    float dealtW = cellW * 0.58f;
                    float takenW = cellW * 0.28f;
                    float dealtX = cellX + cellW * 0.04f;
                    float takenX = dealtX + dealtW + cellW * 0.05f;

                    _cellRects.Add((new Rect2(cellX, plotTop, cellW, plotH), row.Round, s));

                    var agg = AggregateBySource(row.DealtSegs[s]);
                    float yTop = plotBottom;
                    foreach (var a in agg)
                    {
                        float bh = plotH * a.Sum / maxVal;
                        if (bh < 0.5f) continue;
                        var bar = NextBar(ref barCur);
                        bar.Color = new Color(c.R, c.G, c.B, 0.95f);
                        bar.SetPosition(new Vector2(dealtX, yTop - bh));
                        bar.SetSize(new Vector2(dealtW, Math.Max(bh - 1f, 1f)));

                        if (a.Icon != null && bh >= 10 * sc && dealtW >= 14 * sc)
                        {
                            var tex = LoadTex(a.Icon);
                            if (tex != null)
                            {
                                float isz = Math.Min(Math.Min(dealtW - 2f, bh - 2f), maxIcon);
                                var ic = NextIcon(ref iconCur);
                                ic.Texture = tex;
                                ic.SetSize(new Vector2(isz, isz));
                                ic.SetPosition(new Vector2(dealtX + (dealtW - isz) * 0.5f, yTop - bh + (bh - isz) * 0.5f));
                            }
                        }
                        if (a.Count > 1 && bh >= 13 * sc && dealtW >= 16 * sc)
                        {
                            var xn = NextLabel(ref labelCur, F(11, sc), UiTheme.Cream);
                            xn.Text = $"x{a.Count}";
                            xn.SetPosition(new Vector2(dealtX + dealtW - 16 * sc, yTop - bh + 1 * sc));
                        }
                        yTop -= bh;
                    }

                    float th = plotH * row.Taken[s] / maxVal;
                    if (th > 0.5f)
                    {
                        var tb = NextBar(ref barCur);
                        tb.Color = UiTheme.Red;
                        tb.SetPosition(new Vector2(takenX, plotBottom - th));
                        tb.SetSize(new Vector2(takenW, th));
                    }
                }

                var rl = NextLabel(ref labelCur, F(13, sc), new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.6f));
                rl.Text = row.Round.ToString();
                rl.SetPosition(new Vector2(groupX + groupW * 0.5f - 4 * sc, plotBottom + 3 * sc));
            }

            HideRest(_labels, labelCur);
            HideRest(_bars, barCur);
            HideRest(_icons, iconCur);
            HideRest(_grid, gridCur);
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] bars render error: {ex.Message}"); }
    }

    private static int F(int baseSize, float sc) => Math.Max(7, (int)Math.Round(baseSize * sc));

    public static List<(string Label, long Sum, int Count, string? Icon)> AggregateBySource(List<DealtSeg> segs)
    {
        var order = new List<string>();
        var sum = new Dictionary<string, long>();
        var count = new Dictionary<string, int>();
        var icon = new Dictionary<string, string?>();
        foreach (var seg in segs)
        {
            if (!sum.ContainsKey(seg.Label)) { order.Add(seg.Label); sum[seg.Label] = 0; count[seg.Label] = 0; icon[seg.Label] = seg.Icon; }
            sum[seg.Label] += seg.Amount;
            count[seg.Label] += 1;
            if (icon[seg.Label] == null) icon[seg.Label] = seg.Icon;
        }
        var list = new List<(string, long, int, string?)>();
        foreach (var label in order) list.Add((label, sum[label], count[label], icon[label]));
        list.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return list;
    }

    private Label NextLabel(ref int cursor, int fontSize, Color color)
    {
        Label lbl;
        if (cursor < _labels.Count) lbl = _labels[cursor];
        else { lbl = UiTheme.MakeLabel(fontSize, color); _container.AddChild(lbl); _labels.Add(lbl); }
        UiTheme.Apply(lbl, fontSize, color);
        lbl.Visible = true;
        cursor++;
        return lbl;
    }

    private ColorRect NextBar(ref int cursor)
    {
        ColorRect bar;
        if (cursor < _bars.Count) bar = _bars[cursor];
        else { bar = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore }; _barLayer.AddChild(bar); _bars.Add(bar); }
        bar.Visible = true;
        cursor++;
        return bar;
    }

    private ColorRect NextGrid(ref int cursor)
    {
        ColorRect g;
        if (cursor < _grid.Count) g = _grid[cursor];
        else { g = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore }; _gridLayer.AddChild(g); _grid.Add(g); }
        g.Visible = true;
        cursor++;
        return g;
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
            _iconLayer.AddChild(ic);
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

    private static void HideRest<T>(List<T> nodes, int from) where T : CanvasItem
    {
        for (int i = from; i < nodes.Count; i++) nodes[i].Visible = false;
    }
}
