using System;
using Godot;

namespace STS2_DamageCharts;

// Always-on, draggable incoming-damage readout: predicted HP loss this turn (a range when uncertain) and
// the HP you'd be left at. Docked beside the player's own health bar, so it shows numbers only; Block /
// Osty appear only when they change the result. Reads clearly safe when nothing can cost HP, and flags
// lethal when max loss >= current HP. Native nodes only; poll-based mouse from the tick.
// With native_visuals on, the panel is the game's hover-tip frame, the numbers use the game's numeric
// Kreon and lethal pulses the panel with the game's button_pulse shader; any missing asset falls back
// to the hand-built look.
internal sealed class DamageForecastView
{
    private const float BaseWidth = 216f;
    private const float BaseHeight = 86f;
    private const float BaseHeaderH = 20f;
    private const float BaseMargin = 14f;

    private readonly CanvasLayer _layer;
    private readonly Control _container;
    private readonly Panel _shadow;   // hover-tip drop shadow; only shown with the native panel
    private readonly Panel _bg;
    private bool _nativePanel;
    private readonly Label _title;
    private readonly RichTextLabel _loss;
    private readonly RichTextLabel _stats;

    // BBCode hex tags matching the bar segments, so each value reads in its own color.
    private const string BlockHex = "8cb8ff";  // block = sky blue
    private const string OstyHex = "c08cf0";   // Osty = violet
    private const string LossHex = "ff5555";   // loss = red
    private const string GreenHex = "6bd45c";  // safe
    private const string GoldHex = "efc851";   // hidden flag / caution
    private const string CreamHex = "fff6e2";  // default text
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

        _shadow = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _container.AddChild(_shadow);

        _bg = UiTheme.MakePanel(0.5f, 1f);
        _bg.MouseFilter = Control.MouseFilterEnum.Stop;
        _bg.SetPosition(Vector2.Zero);
        _container.AddChild(_bg);

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

    public bool IsValid() => GodotObject.IsInstanceValid(_layer);
    public void Hide() { if (IsValid()) _layer.Visible = false; }
    public void Dispose() { if (IsValid()) _layer.QueueFree(); }
    public Vector2 PositionFraction => _posFrac;
    public bool IsDragging => _dragging;
    // Docked mode: sit beside the local player's creature (AnchorRect, viewport coords; null = not found,
    // fall back to the saved fraction). A drag that moves the panel turns docking off.
    public bool AnchorToPlayer { get; set; } = true;
    public Rect2? AnchorRect { get; set; }

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
                if (_dragging) { _dragging = false; if (moved) { savedNow = true; AnchorToPlayer = false; } }
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
            if (!_nativePanel) _nativePanel = ForecastStyle.TryNativePanel(_bg, _shadow);
            ForecastStyle.SizeFrame(_bg, _shadow, _w, _h, sc, _nativePanel);
            if (!_nativePanel) UiTheme.RestylePanel(_bg, 0.5f, sc);
            Font? numFont = GameAssets.NumericFont;  // null when native_visuals is off or the font is missing

            float inset = TopInset(_vp);
            _topInset = inset;
            if (!_posFracInit)
            {
                _posFrac = new Vector2(_vp.X > 0 ? margin / _vp.X : 0.02f,
                                       _vp.Y > 0 ? Math.Max(margin, inset) / _vp.Y : 0.02f);
                _posFracInit = true;
            }
            if (AnchorToPlayer && !_dragging && AnchorRect is Rect2 hb && _vp.X > 0 && _vp.Y > 0)
            {
                // Left of the player's hitbox, bottom-aligned with it (the HP bar sits just below); flip to
                // the right side if the player stands too close to the left edge. Kept as the fraction so a
                // drag starts from where the panel is drawn.
                float gap = 16f * sc;
                float ax = hb.Position.X - gap - _w;
                if (ax < margin) ax = hb.End.X + gap;
                float ay = hb.End.Y - _h;
                _posFrac = new Vector2(ax / _vp.X, ay / _vp.Y);
            }
            float px = Math.Clamp(_posFrac.X * _vp.X, 0f, Math.Max(0f, _vp.X - _w));
            float py = Math.Clamp(_posFrac.Y * _vp.Y, inset, Math.Max(inset, _vp.Y - _h));
            _container.SetPosition(new Vector2(px, py));

            UiTheme.Apply(_title, F(13, sc), new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.7f));
            ForecastStyle.SetFont(_title, GameAssets.KreonBold);
            // The hover-tip frame is ~8px thick, so nudge the title inside it.
            _title.SetPosition(_nativePanel ? new Vector2(14 * sc, 7 * sc) : new Vector2(10 * sc, 4 * sc));

            string lossStr = r.MinLoss == r.MaxLoss ? r.MinLoss.ToString() : $"{r.MinLoss}–{r.MaxLoss}";
            float rowY = _headerH + 4 * sc;

            // Hero: predicted HP loss (big) plus the HP you're left at afterwards (worst case). Together
            // they answer "how much will I lose" and "what will I be sitting at".
            // Don't clamp at 0 when lethal: the net-negative HP is how much you still need to block/mitigate
            // to survive (e.g. "30 → -11" means you're 11 short).
            int after = r.CurrentHp - r.MaxLoss;
            string afterHex = after <= 0 ? LossHex
                : (r.MaxHp > 0 && after < r.MaxHp * 0.30f ? GoldHex : GreenHex);
            string tail = after <= 0 ? "☠" : "left";
            string hidden = r.HasHidden ? $" [color=#{GoldHex}][font_size={F(15, sc)}]+?[/font_size][/color]" : "";
            string line1 = r.IsSafe
                ? $"[color=#{GreenHex}]Safe ✓[/color]"
                : $"[color=#{LossHex}]{lossStr} [font_size={F(15, sc)}]HP[/font_size][/color]{hidden}";
            string line2 = $"[color=#{CreamHex}][font_size={F(13, sc)}]{r.CurrentHp} → [color=#{afterHex}]{after}[/color] {tail}[/font_size][/color]";
            UiTheme.Apply(_loss, F(30, sc), UiTheme.Red);
            if (numFont != null)
            {
                // Numbers in the game's numeric Kreon with the health bar HP label's dark-red outline.
                ForecastStyle.SetFont(_loss, numFont);
                ForecastStyle.Outline(_loss, r.IsSafe ? UiTheme.Outline : ForecastStyle.HpLabelOutline, F(6, sc));
                ForecastStyle.Shadow(_loss, sc);
            }
            _loss.Text = line1 + "\n" + line2;
            _loss.SetPosition(new Vector2(10 * sc, rowY));

            // Tucked micro-ledger on the right, only when mitigation changes the result (otherwise In equals
            // the loss already shown): incoming vs the Block / Osty absorbing it.
            bool mitigated = r.Incoming > 0 && (r.Block > 0 || r.OstyAbsorb > 0);
            string led = "";
            if (mitigated)
            {
                led = $"In {r.Incoming}";
                if (r.Block > 0) led += $"\n[color=#{BlockHex}]Blk {r.Block}[/color]";
                if (r.OstyAbsorb > 0) led += $"\n[color=#{OstyHex}]Osty {r.OstyAbsorb}[/color]";
            }
            _stats.Visible = mitigated;
            UiTheme.Apply(_stats, F(12, sc), UiTheme.Cream);
            if (numFont != null) { ForecastStyle.SetFont(_stats, numFont); ForecastStyle.Shadow(_stats, sc); }
            _stats.Text = led;
            _stats.SetPosition(new Vector2(_w - 72 * sc, rowY));

            // Lethal: red border tint on the panel to signal you can die this turn, pulsed with the game's
            // button_pulse shader when available (the tint alone is the fallback).
            _bg.SelfModulate = r.IsLethal ? new Color(1f, 0.5f, 0.5f) : Colors.White;
            Material? pulse = r.IsLethal ? ForecastStyle.PulseMaterial() : null;
            if (_bg.Material != pulse) _bg.Material = pulse;
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] forecast render error: {ex.Message}"); }
    }

    private static int F(int baseSize, float sc) => Math.Max(8, (int)Math.Round(baseSize * sc));
}

// Native-look helpers shared by the forecast panel and its ledger. Every member returns null/false or
// no-ops when native_visuals is off or an asset is missing, so callers keep their current look.
internal static class ForecastStyle
{
    public const string UnknownIntentTex = "res://images/packed/intents/intent_unknown.png";

    // health_bar.tscn node colors and NHealthBar's static colors (sts2.dll .cctor).
    public static readonly Color HpLabelOutline = new(0.564706f, 0f, 0f);                          // "900000"
    public static readonly Color TextShadow = new(0f, 0f, 0f, 0.25098f);                           // labels' font_shadow_color

    // The game's hover-tip frame (GameAssets.NativePanel) plus its drop shadow. Returns false (and leaves
    // the panel alone) when the art is unavailable, so the caller keeps restyling with UiTheme.
    // The hover-tip art samples a region 19x11px larger than the texture (transparent right/bottom), and its
    // 9-slice margins are texture pixels. Like NativeLook.LayoutFrame: draw at 1x plus that overhang and scale
    // the node, so the VISIBLE frame is exactly w x h. Fallback panels keep their plain w x h size.
    public static void SizeFrame(Panel bg, Panel shadow, float w, float h, float sc, bool native)
    {
        var scale = native ? new Vector2(sc, sc) : Vector2.One;
        var size = native ? new Vector2(w / sc + NativeLook.OverhangX, h / sc + NativeLook.OverhangY) : new Vector2(w, h);
        bg.Scale = scale; bg.SetSize(size);
        shadow.Scale = scale; shadow.SetSize(size);
    }

    public static bool TryNativePanel(Panel bg, Panel shadow)
    {
        try
        {
            var box = GameAssets.NativePanel();
            if (box == null) { shadow.Visible = false; return false; }
            bg.AddThemeStyleboxOverride("panel", box);
            var sh = GameAssets.NativePanelShadow();
            if (sh != null) shadow.AddThemeStyleboxOverride("panel", sh);
            shadow.Visible = sh != null;
            return true;
        }
        catch { return false; }
    }

    public static void SetFont(Label lbl, Font? f) { if (f != null) lbl.AddThemeFontOverride("font", f); }
    public static void SetFont(RichTextLabel rtl, Font? f) { if (f != null) rtl.AddThemeFontOverride("normal_font", f); }

    public static void Outline(Control c, Color color, int size)
    {
        c.AddThemeColorOverride("font_outline_color", color);
        c.AddThemeConstantOverride("outline_size", Math.Max(0, size));
    }

    // The soft offset shadow the game's labels carry (hover tips use 3,2).
    public static void Shadow(Control c, float sc)
    {
        c.AddThemeColorOverride("font_shadow_color", TextShadow);
        c.AddThemeConstantOverride("shadow_offset_x", Math.Max(1, (int)Math.Round(3 * sc)));
        c.AddThemeConstantOverride("shadow_offset_y", Math.Max(1, (int)Math.Round(2 * sc)));
    }

    // shaders/button_pulse.gdshader: COLOR.rgb += |sin(TIME*2)| * 0.2 * pulse_strength. NPeekButton drives
    // pulse_strength with IsPeeking (0/1); we hold it at 1 while lethal.
    private static ShaderMaterial? _pulse;
    public static ShaderMaterial? PulseMaterial()
    {
        if (!GameAssets.Enabled) return null;
        if (_pulse != null) return _pulse;
        try
        {
            var sh = GameAssets.Shader(GameAssets.Paths.ButtonPulseShader);
            if (sh == null) return null;
            var m = new ShaderMaterial { Shader = sh };
            m.SetShaderParameter("pulse_strength", 1f);
            _pulse = m;
        }
        catch { }
        return _pulse;
    }
}
