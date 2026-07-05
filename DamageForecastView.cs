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
    private const float BaseHeight = 104f;
    private const float BaseHeaderH = 22f;
    private const float BaseBarH = 22f;
    private const float BaseMargin = 14f;

    private static readonly Color Green = new(0.42f, 0.83f, 0.36f);
    private static readonly Color Shield = new(0.55f, 0.72f, 1f, 0.7f);      // block = sky blue
    private static readonly Color OstyColor = new(0.72f, 0.45f, 0.90f, 0.7f); // Osty = violet
    private static readonly Color LossMin = UiTheme.Red;
    private static readonly Color LossMax = new(UiTheme.Red.R, UiTheme.Red.G, UiTheme.Red.B, 0.40f);
    private static readonly Color TrackBg = new(0f, 0f, 0f, 0.35f);

    private readonly CanvasLayer _layer;
    private readonly Control _container;
    private readonly Panel _bg;
    private readonly Control _barLayer;
    private readonly Label _title;
    private readonly RichTextLabel _loss;
    private readonly RichTextLabel _stats;

    // BBCode hex tags matching the bar segments, so each value reads in its own color.
    private const string BlockHex = "8cb8ff";  // block = sky blue
    private const string OstyHex = "c08cf0";   // Osty = violet
    private const string LossHex = "ff5555";   // loss = red
    private const string GreenHex = "6bd45c";  // safe
    private const string GoldHex = "efc851";   // hidden flag
    private readonly ColorRect _track;
    private readonly ColorRect _shield;
    private readonly ColorRect _osty;
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
        _osty = NewRect(OstyColor);
        _green = NewRect(Green);
        _lossMax = NewRect(LossMax);   // painted before min so min sits on top
        _lossMin = NewRect(LossMin);

        _title = UiTheme.MakeLabel(12, new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.85f));
        _title.Text = "Incoming Forecast";
        _container.AddChild(_title);

        _loss = NewRich(24, UiTheme.Red);
        _stats = NewRich(11, UiTheme.Cream);

        if (savedPosFrac.HasValue) { _posFrac = savedPosFrac.Value; _posFracInit = true; }
    }

    private RichTextLabel NewRich(int fontSize, Color color)
    {
        var rtl = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        UiTheme.Apply(rtl, fontSize, color);
        _container.AddChild(rtl);
        return rtl;
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
            string hidden = r.HasHidden ? $"  [color=#{GoldHex}]+?[/color]" : "";
            string block = $"[color=#{BlockHex}]Block {r.Block}[/color]";
            string osty = r.OstyAbsorb > 0 ? $"   [color=#{OstyHex}]Osty {r.OstyAbsorb}[/color]" : "";

            // Hero line: the predicted HP loss — the number the player decides on. Big and color-coded.
            string lossBig = r.IsSafe
                ? $"[color=#{GreenHex}]Safe ✓[/color]"
                : $"[color=#{LossHex}]−{lossStr} HP{(r.IsLethal ? "  ☠" : "")}[/color]";
            UiTheme.Apply(_loss, F(26, sc), UiTheme.Red);
            _loss.Text = lossBig;
            _loss.SetPosition(new Vector2(8 * sc, _headerH + 1 * sc));

            // Supporting line: the components behind the loss, smaller, just above the bar.
            UiTheme.Apply(_stats, F(12, sc), UiTheme.Cream);
            _stats.Text = $"In {r.Incoming}{hidden}   {block}{osty}";
            float barY = _h - BaseBarH * sc - 8 * sc;
            _stats.SetPosition(new Vector2(8 * sc, barY - 17 * sc));

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

        int hp = Math.Max(0, r.CurrentHp);
        int block = Math.Max(0, r.Block);
        int ostyPool = Math.Max(0, r.OstyAbsorb);
        int denom = Math.Max(1, block + ostyPool + hp);
        float blockW = barW * block / denom;
        float ostyW = barW * ostyPool / denom;
        float hpW = barW * hp / denom;
        float hpX = x0 + blockW + ostyW;

        // Track background under the whole bar.
        Place(_track, x0, barY, barW, barH);

        // Shield buffer, split into distinct segments: block (blue) then Osty (violet).
        Place(_shield, x0, barY, blockW, barH);
        Place(_osty, x0 + blockW, barY, ostyW, barH);

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
