using System;
using Godot;

namespace STS2_DamageCharts;

// Always-on, draggable HP-centric threat bar. Left shield segment = block (consumed portion darker);
// HP track = current HP with predicted loss eating in from the right (solid red = guaranteed min,
// translucent red = uncertain up to max). Reads clearly safe when nothing can cost HP, and flags
// lethal when max loss >= current HP. Native nodes only; poll-based mouse from the tick.
internal sealed class DamageForecastView
{
    private const float BaseWidth = 300f;
    private const float BaseHeight = 76f;
    private const float BaseHeaderH = 22f;
    private const float BaseBarH = 22f;
    private const float BaseMargin = 14f;

    private static readonly Color Green = new(0.42f, 0.83f, 0.36f);
    private static readonly Color Shield = new(0.55f, 0.72f, 1f, 0.55f);
    private static readonly Color ShieldUsed = new(0.30f, 0.45f, 0.75f, 0.85f);
    private static readonly Color LossMin = UiTheme.Red;
    private static readonly Color LossMax = new(UiTheme.Red.R, UiTheme.Red.G, UiTheme.Red.B, 0.40f);
    private static readonly Color TrackBg = new(0f, 0f, 0f, 0.35f);

    private readonly CanvasLayer _layer;
    private readonly Control _container;
    private readonly Panel _bg;
    private readonly Control _barLayer;
    private readonly Label _title;
    private readonly Label _stats;
    private readonly ColorRect _track;
    private readonly ColorRect _shield;
    private readonly ColorRect _shieldUsed;
    private readonly ColorRect _green;
    private readonly ColorRect _lossMin;
    private readonly ColorRect _lossMax;

    public float UiScaleMul = 1f;
    public ForecastResult? LastResult { get; private set; }

    private Vector2 _posFrac;
    private bool _posFracInit;
    private bool _dragging;
    private bool _leftLast;
    private Vector2 _dragOffset;
    private Vector2 _pressStart;

    private Vector2 _vp = new(1920, 1080);
    private float _w = BaseWidth, _h = BaseHeight, _headerH = BaseHeaderH;
    private float _topInset;

    public DamageForecastView(Node root, Vector2? savedPosFrac)
    {
        _layer = new CanvasLayer { Layer = 129, Name = "Sts2DamageChartsForecast" };
        root.AddChild(_layer);

        _container = new Control { Name = "Forecast", MouseFilter = Control.MouseFilterEnum.Ignore };
        _layer.AddChild(_container);

        _bg = UiTheme.MakePanel(0.5f, 1f);
        _bg.MouseFilter = Control.MouseFilterEnum.Stop;
        _bg.SetPosition(Vector2.Zero);
        _container.AddChild(_bg);

        _barLayer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _barLayer.SetPosition(Vector2.Zero);
        _container.AddChild(_barLayer);

        _track = NewRect(TrackBg);
        _shield = NewRect(Shield);
        _shieldUsed = NewRect(ShieldUsed);
        _green = NewRect(Green);
        _lossMax = NewRect(LossMax);   // painted before min so min sits on top
        _lossMin = NewRect(LossMin);

        _title = UiTheme.MakeLabel(12, new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.85f));
        _title.Text = "Incoming Forecast";
        _container.AddChild(_title);

        _stats = UiTheme.MakeLabel(12, UiTheme.Cream);
        _container.AddChild(_stats);

        if (savedPosFrac.HasValue) { _posFrac = savedPosFrac.Value; _posFracInit = true; }
    }

    private ColorRect NewRect(Color c)
    {
        var r = new ColorRect { Color = c, MouseFilter = Control.MouseFilterEnum.Ignore };
        _barLayer.AddChild(r);
        return r;
    }

    public bool IsValid() => GodotObject.IsInstanceValid(_layer);
    public void Hide() { if (IsValid()) _layer.Visible = false; }
    public void Dispose() { if (IsValid()) _layer.QueueFree(); }
    public Vector2 PositionFraction => _posFrac;
    public bool IsDragging => _dragging;

    public bool IsHovered()
    {
        if (!IsValid() || _dragging) return false;
        try
        {
            Vector2 local = _container.GetLocalMousePosition();
            return local.X >= 0 && local.X <= _w && local.Y >= 0 && local.Y <= _h;
        }
        catch { return false; }
    }

    private static float TopInset(Vector2 vp) => Math.Max(48f, vp.Y * 0.09f);

    public bool UpdateMouse(bool leftDown)
    {
        if (!IsValid()) return false;
        bool savedNow = false;
        try
        {
            Vector2 global = _container.GetGlobalMousePosition();
            Vector2 local = _container.GetLocalMousePosition();
            bool overHeader = local.X >= 0 && local.X <= _w && local.Y >= 0 && local.Y <= _headerH;

            if (leftDown && !_leftLast)
            {
                _pressStart = global;
                if (overHeader) { _dragging = true; _dragOffset = global - new Vector2(_posFrac.X * _vp.X, _posFrac.Y * _vp.Y); }
            }
            else if (leftDown && _dragging)
            {
                Vector2 abs = global - _dragOffset;
                float yMin = _vp.Y > 0 ? _topInset / _vp.Y : 0f;
                float x = _vp.X > 0 ? Math.Clamp(abs.X / _vp.X, 0f, Math.Max(0f, 1f - _w / _vp.X)) : 0f;
                float y = _vp.Y > 0 ? Math.Clamp(abs.Y / _vp.Y, yMin, Math.Max(yMin, 1f - _h / _vp.Y)) : 0f;
                _posFrac = new Vector2(x, y);
            }
            else if (!leftDown && _leftLast)
            {
                bool moved = (global - _pressStart).Length() > 5f;
                if (_dragging) { _dragging = false; if (moved) savedNow = true; }
            }
            _leftLast = leftDown;
        }
        catch { }
        return savedNow;
    }

    public void Render(ForecastResult r)
    {
        if (!IsValid()) return;
        try
        {
            _layer.Visible = true;
            LastResult = r;

            _vp = _container.GetViewportRect().Size;
            float sc = UiTheme.Scale(_vp) * Math.Clamp(UiScaleMul, 0.5f, 4f);
            _w = BaseWidth * sc; _h = BaseHeight * sc; _headerH = BaseHeaderH * sc;
            float margin = BaseMargin * sc;

            _container.SetSize(new Vector2(_w, _h));
            _bg.SetSize(new Vector2(_w, _h));
            _barLayer.SetSize(new Vector2(_w, _h));
            UiTheme.RestylePanel(_bg, 0.5f, sc);

            float inset = TopInset(_vp);
            _topInset = inset;
            if (!_posFracInit)
            {
                _posFrac = new Vector2(_vp.X > 0 ? margin / _vp.X : 0.02f,
                                       _vp.Y > 0 ? Math.Max(margin, inset) / _vp.Y : 0.02f);
                _posFracInit = true;
            }
            float px = Math.Clamp(_posFrac.X * _vp.X, 0f, Math.Max(0f, _vp.X - _w));
            float py = Math.Clamp(_posFrac.Y * _vp.Y, inset, Math.Max(inset, _vp.Y - _h));
            _container.SetPosition(new Vector2(px, py));

            UiTheme.Apply(_title, F(13, sc), new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.85f));
            _title.SetPosition(new Vector2(8 * sc, 4 * sc));

            string lossStr = r.MinLoss == r.MaxLoss ? r.MinLoss.ToString() : $"{r.MinLoss}–{r.MaxLoss}";
            string hidden = r.HasHidden ? "  +?" : "";
            string statsText = r.IsSafe
                ? $"Safe ✓   Block {r.Block}{hidden}"
                : $"In {r.Incoming}   Block {r.Block}   Loss {lossStr}{hidden}";
            var statColor = r.IsLethal ? UiTheme.Red : (r.IsSafe ? Green : UiTheme.Cream);
            UiTheme.Apply(_stats, F(13, sc), statColor);
            _stats.Text = statsText;
            _stats.SetPosition(new Vector2(8 * sc, _headerH + 2 * sc));

            LayoutBar(r, sc);

            // Lethal: red border tint on the panel to signal you can die this turn.
            _bg.SelfModulate = r.IsLethal ? new Color(1f, 0.5f, 0.5f) : Colors.White;
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] forecast render error: {ex.Message}"); }
    }

    private void LayoutBar(ForecastResult r, float sc)
    {
        float barH = BaseBarH * sc;
        float barY = _h - barH - 8 * sc;
        float x0 = 8 * sc;
        float barW = _w - 16 * sc;

        int block = Math.Max(0, r.Block);
        int hp = Math.Max(0, r.CurrentHp);
        int denom = Math.Max(1, block + hp);
        float shieldW = barW * block / denom;
        float hpW = barW * hp / denom;
        float hpX = x0 + shieldW;

        // Track background under the whole bar.
        Place(_track, x0, barY, barW, barH);

        // Shield buffer (block); consumed portion (mitigated) rendered darker on its left.
        Place(_shield, x0, barY, shieldW, barH);
        float usedW = block > 0 ? shieldW * Math.Min(block, r.Mitigated) / block : 0f;
        Place(_shieldUsed, x0, barY, usedW, barH);

        // HP track: loss eats in from the right. minLoss (solid) and up-to-max (translucent) clamp to HP.
        int minLossHp = Math.Clamp(r.MinLoss, 0, hp);
        int maxLossHp = Math.Clamp(r.MaxLoss, 0, hp);
        float wPerHp = hp > 0 ? hpW / hp : 0f;

        float greenW = (hp - maxLossHp) * wPerHp;
        Place(_green, hpX, barY, greenW, barH);

        float maxLossW = maxLossHp * wPerHp;
        Place(_lossMax, hpX + hpW - maxLossW, barY, maxLossW, barH);

        float minLossW = minLossHp * wPerHp;
        Place(_lossMin, hpX + hpW - minLossW, barY, minLossW, barH);
    }

    private static void Place(ColorRect r, float x, float y, float w, float h)
    {
        bool vis = w > 0.5f && h > 0.5f;
        r.Visible = vis;
        if (!vis) return;
        r.SetPosition(new Vector2(x, y));
        r.SetSize(new Vector2(w, h));
    }

    private static int F(int baseSize, float sc) => Math.Max(8, (int)Math.Round(baseSize * sc));
}
