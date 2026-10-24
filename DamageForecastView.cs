using System;
using Godot;

namespace STS2_DamageCharts;

// Always-on, draggable HP-centric threat bar. Left shield segment = block (consumed portion darker);
// HP track = current HP with predicted loss eating in from the right (solid red = guaranteed min,
// translucent red = uncertain up to max). Reads clearly safe when nothing can cost HP, and flags
// lethal when max loss >= current HP. Native nodes only; poll-based mouse from the tick.
// With native_visuals on, the panel is the game's hover-tip frame, the numbers use the game's numeric
// Kreon, the bar is the game's own health bar (NativeHealthBar) and lethal pulses the panel with the
// game's button_pulse shader; any missing asset falls back to the hand-built look below.
internal sealed class DamageForecastView
{
    private const float BaseWidth = 216f;
    private const float BaseHeight = 124f;
    private const float BaseHeaderH = 20f;
    private const float BaseBarH = 18f;
    private const float BaseMargin = 14f;

    private static readonly Color Green = new(0.42f, 0.83f, 0.36f);
    private static readonly Color Shield = new(0.55f, 0.72f, 1f, 0.7f);      // block = sky blue
    private static readonly Color OstyColor = new(0.72f, 0.45f, 0.90f, 0.7f); // Osty = violet
    private static readonly Color LossMin = UiTheme.Red;
    private static readonly Color LossMax = new(UiTheme.Red.R, UiTheme.Red.G, UiTheme.Red.B, 0.40f);
    private static readonly Color TrackBg = new(0f, 0f, 0f, 0.35f);

    private readonly CanvasLayer _layer;
    private readonly Control _container;
    private readonly Panel _shadow;   // hover-tip drop shadow; only shown with the native panel
    private readonly Panel _bg;
    private readonly Control _barLayer;
    private NativeHealthBar? _nativeBar;
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

        _shadow = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _container.AddChild(_shadow);

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
            _shadow.SetSize(new Vector2(_w, _h));
            _barLayer.SetSize(new Vector2(_w, _h));
            if (!_nativePanel) _nativePanel = ForecastStyle.TryNativePanel(_bg, _shadow);
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
            string line1 = r.IsSafe
                ? $"[color=#{GreenHex}]Safe ✓[/color]"
                : $"[color=#{LossHex}]{lossStr} [font_size={F(15, sc)}]HP[/font_size][/color]";
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

            // Tucked micro-ledger on the right: the components behind the loss, small and color-coded.
            string hidden = r.HasHidden ? $" [color=#{GoldHex}]+?[/color]" : "";
            string led = $"In {r.Incoming}{hidden}\n[color=#{BlockHex}]Blk {r.Block}[/color]";
            if (r.OstyAbsorb > 0) led += $"\n[color=#{OstyHex}]Osty {r.OstyAbsorb}[/color]";
            UiTheme.Apply(_stats, F(12, sc), UiTheme.Cream);
            if (numFont != null) { ForecastStyle.SetFont(_stats, numFont); ForecastStyle.Shadow(_stats, sc); }
            _stats.Text = led;
            _stats.SetPosition(new Vector2(_w - 72 * sc, rowY));

            if (_nativeBar == null && GameAssets.Enabled) _nativeBar = NativeHealthBar.TryCreate(_barLayer);
            if (_nativeBar == null || !LayoutNativeBar(r, sc)) LayoutBar(r, sc);

            // Lethal: red border tint on the panel to signal you can die this turn, pulsed with the game's
            // button_pulse shader when available (the tint alone is the fallback).
            _bg.SelfModulate = r.IsLethal ? new Color(1f, 0.5f, 0.5f) : Colors.White;
            Material? pulse = r.IsLethal ? ForecastStyle.PulseMaterial() : null;
            if (_bg.Material != pulse) _bg.Material = pulse;
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] forecast render error: {ex.Message}"); }
    }

    // The game's health bar in place of the ColorRect bar. Leaves room on the left for its block icon.
    private bool LayoutNativeBar(ForecastResult r, float sc)
    {
        float barH = BaseBarH * sc;
        float barY = _h - barH - 12 * sc;
        float x0 = 26 * sc;
        if (!_nativeBar!.Layout(r, x0, barY, _w - x0 - 12 * sc, barH, sc)) { _nativeBar.Visible = false; return false; }
        _nativeBar.Visible = true;
        _track.Visible = _shield.Visible = _osty.Visible = _green.Visible = _lossMin.Visible = _lossMax.Visible = false;
        return true;
    }

    private void LayoutBar(ForecastResult r, float sc)
    {
        if (_nativeBar != null) _nativeBar.Visible = false;
        float barH = BaseBarH * sc;
        float barY = _h - barH - 12 * sc;
        float x0 = 12 * sc;
        float barW = _w - 24 * sc;

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

// Native-look helpers shared by the forecast panel and its ledger. Every member returns null/false or
// no-ops when native_visuals is off or an asset is missing, so callers keep their current look.
internal static class ForecastStyle
{
    // scenes/combat/health_bar.tscn textures (real files in the v0.107.1 pack).
    public const string HpBgTex = "res://images/ui/combat/health_bar_bg.png";
    public const string HpStrokeTex = "res://images/ui/combat/health_bar_stroke.png";
    public const string HpMaskTex = "res://images/ui/combat/health_bar.png";
    public const string HpFillTex = "res://images/ui/combat/health_bar_fill.png";
    public const string BlockIconTex = "res://images/ui/combat/block.png";
    public const string UnknownIntentTex = "res://images/packed/intents/intent_unknown.png";

    // health_bar.tscn node colors and NHealthBar's static colors (sts2.dll .cctor).
    public static readonly Color HpBgModulate = new(0.164706f, 0.376471f, 0.419608f, 0.752941f); // HpBackground
    public static readonly Color StrokeModulate = new(0.705882f, 0.886275f, 1f, 1f);              // BlockOutline
    public static readonly Color HpFill = new(0.8f, 0.0470588f, 0.0470588f);                       // HpForeground
    public static readonly Color BlockFill = new(0.231373f, 0.435294f, 0.639216f);                 // "3B6FA3" HP-with-block
    public static readonly Color HpLabelOutline = new(0.564706f, 0f, 0f);                          // "900000"
    public static readonly Color BlockLabelOutline = new(0.042f, 0.3318f, 0.42f);                  // BlockLabel
    public static readonly Color DoomLethalFont = new(0.984314f, 0.552941f, 1f);                   // "FB8DFF"
    public static readonly Color DoomLethalOutline = new(0.176471f, 0.070588f, 0.388235f);         // "2D1263"
    public static readonly Color TextShadow = new(0f, 0f, 0f, 0.25098f);                           // labels' font_shadow_color
    public static readonly Color TitleGold = new(0.937255f, 0.784314f, 0.317647f);                 // hover_tip Title

    // The game's hover-tip frame (GameAssets.NativePanel) plus its drop shadow. Returns false (and leaves
    // the panel alone) when the art is unavailable, so the caller keeps restyling with UiTheme.
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

    // The material NHealthBar's DoomForeground uses to preview incoming Doom: doom_bar.gdshader, which
    // replaces the fill's color with gradient_tex sampled by two scrolling noise_tex lookups (alpha kept).
    // Taken straight from health_bar.tscn's packed state so the noise/gradient match exactly; else rebuilt
    // from the shader with the scene's values. Null when neither is available.
    private static Material? _doom;
    private static bool _doomSceneScanned; // scan the scene's state once; the shader rebuild covers a miss
    public static Material? DoomMaterial()
    {
        if (!GameAssets.Enabled) return null;
        if (_doom != null) return _doom;
        try { _doom = DoomFromScene() ?? DoomFromShader(); } catch { _doom = null; }
        return _doom;
    }

    private static Material? DoomFromScene()
    {
        if (_doomSceneScanned) return null;
        try
        {
            var st = GameAssets.Scene(GameAssets.Paths.HealthBarScene)?.GetState();
            if (st == null) return null;
            _doomSceneScanned = true;
            for (int i = 0; i < st.GetNodeCount(); i++)
            {
                if (st.GetNodeName(i).ToString() != "DoomForeground") continue;
                for (int p = 0; p < st.GetNodePropertyCount(i); p++)
                    if (st.GetNodePropertyName(i, p).ToString() == "material"
                        && st.GetNodePropertyValue(i, p).AsGodotObject() is ShaderMaterial sm && sm.Shader != null)
                        return sm;
            }
        }
        catch { }
        return null;
    }

    private static Material? DoomFromShader()
    {
        var sh = GameAssets.Shader(GameAssets.Paths.DoomBarShader);
        if (sh == null) return null;
        // health_bar.tscn: FastNoiseLite noise_type=3 (Perlin) frequency 0.0383; 3-stop purple gradient.
        var noise = new NoiseTexture2D { Noise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Frequency = 0.0383f } };
        var grad = new Gradient
        {
            Offsets = new[] { 0f, 0.514583f, 1f },
            Colors = new[]
            {
                new Color(0.300863f, 0.162626f, 0.528347f),
                new Color(0.513726f, 0.254902f, 0.505882f),
                new Color(0.354657f, 0.0421873f, 0.437114f),
            },
        };
        var m = new ShaderMaterial { Shader = sh };
        m.SetShaderParameter("noise_tex", noise);
        m.SetShaderParameter("gradient_tex", new GradientTexture1D { Gradient = grad });
        return m;
    }
}

// The game's combat health bar (scenes/combat/health_bar.tscn) rebuilt from its own textures:
//   HpBarContainer -> HpBackground (health_bar_bg, teal-tinted), BlockOutline (health_bar_stroke, shown while
//   you have block), HpForegroundContainer (inset 5,3, clips) -> Mask (health_bar.png, clips) -> fills
//   (health_bar_fill, 4px taller than the mask), HpLabel "cur/max" (numeric Kreon, dark-red outline), and
//   the BlockContainer icon + number at the bar's left end.
// Our fills, left to right: block (game's HP-with-block blue), Osty (violet), surviving HP (HpForeground
// red), then predicted loss eating in from the right drawn like DoomForeground (doom_bar.gdshader): the
// guaranteed minimum opaque, the uncertain tail up to the maximum at reduced alpha.
internal sealed class NativeHealthBar
{
    private const float GameBarH = 16f; // health_bar.tscn HpBarContainer height; insets scale off it
    private static readonly Color OstyFill = new(0.72f, 0.45f, 0.90f);
    private const float TailAlpha = 0.45f;

    private readonly Control _root;
    private readonly NinePatchRect _bg;
    private readonly NinePatchRect? _stroke;
    private readonly Control _clip;
    private readonly NinePatchRect _mask;
    private readonly NinePatchRect _block, _osty, _hp, _lossMax, _lossMin;
    private readonly Label _hpLabel;
    private readonly TextureRect? _blockIcon;
    private readonly Label _blockLabel;

    public static NativeHealthBar? TryCreate(Control parent)
    {
        try
        {
            var bg = GameAssets.Texture(ForecastStyle.HpBgTex);
            var mask = GameAssets.Texture(ForecastStyle.HpMaskTex);
            var fill = GameAssets.Texture(ForecastStyle.HpFillTex);
            if (bg == null || mask == null || fill == null) return null;
            return new NativeHealthBar(parent, bg, mask, fill,
                GameAssets.Texture(ForecastStyle.HpStrokeTex), GameAssets.Texture(ForecastStyle.BlockIconTex));
        }
        catch { return null; }
    }

    private NativeHealthBar(Control parent, Texture2D bg, Texture2D mask, Texture2D fill, Texture2D? stroke, Texture2D? blockIcon)
    {
        _root = new Control { Name = "NativeHealthBar", MouseFilter = Control.MouseFilterEnum.Ignore };
        _bg = Patch(bg, 9, ForecastStyle.HpBgModulate);
        _root.AddChild(_bg);
        if (stroke != null) { _stroke = Patch(stroke, 9, ForecastStyle.StrokeModulate); _root.AddChild(_stroke); }

        // The scene's HpForegroundContainer is clip_children=Only on a node that draws nothing; we leave it as
        // a plain positioning box and let the Mask (which does draw) do the clipping.
        _clip = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.AddChild(_clip);
        _mask = Patch(mask, 6, Colors.White);
        _mask.ClipChildren = CanvasItem.ClipChildrenMode.Only;
        _clip.AddChild(_mask);

        _block = Fill(fill, ForecastStyle.BlockFill);
        _osty = Fill(fill, OstyFill);
        _hp = Fill(fill, ForecastStyle.HpFill);
        _lossMax = Fill(fill, new Color(UiTheme.Red.R, UiTheme.Red.G, UiTheme.Red.B, 0.40f)); // under min
        _lossMin = Fill(fill, UiTheme.Red);

        _hpLabel = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _root.AddChild(_hpLabel);

        if (blockIcon != null)
        {
            _blockIcon = new TextureRect
            {
                Texture = blockIcon,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            };
            _root.AddChild(_blockIcon);
        }
        _blockLabel = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _root.AddChild(_blockLabel);

        parent.AddChild(_root);
    }

    private static NinePatchRect Patch(Texture2D tex, int lr, Color modulate)
    {
        var np = new NinePatchRect
        {
            Texture = tex,
            PatchMarginLeft = lr,
            PatchMarginRight = lr,
            Modulate = modulate,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        return np;
    }

    private NinePatchRect Fill(Texture2D tex, Color selfModulate)
    {
        var np = Patch(tex, 6, Colors.White);
        np.SelfModulate = selfModulate;
        _mask.AddChild(np);
        return np;
    }

    public bool Visible
    {
        get => GodotObject.IsInstanceValid(_root) && _root.Visible;
        set { if (GodotObject.IsInstanceValid(_root)) _root.Visible = value; }
    }

    public bool Layout(ForecastResult r, float x, float y, float w, float h, float sc)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(_root) || w < 24f || h < 4f) return false;
            float s = h / GameBarH;
            _root.SetPosition(new Vector2(x, y));
            _root.SetSize(new Vector2(w, h));

            // HpBackground / BlockOutline: full container, inset 1px horizontally.
            _bg.SetPosition(new Vector2(1 * s, 0));
            _bg.SetSize(new Vector2(w - 2 * s, h));
            int block = Math.Max(0, r.Block);
            if (_stroke != null)
            {
                _stroke.Visible = block > 0;
                _stroke.SetPosition(new Vector2(1 * s, 0));
                _stroke.SetSize(new Vector2(w - 2 * s, h));
            }

            // HpForegroundContainer: inset 5 left/right, 3 top/bottom; the Mask fills it.
            float mw = Math.Max(0f, w - 10 * s), mh = Math.Max(0f, h - 6 * s);
            _clip.SetPosition(new Vector2(5 * s, 3 * s));
            _clip.SetSize(new Vector2(mw, mh));
            _mask.SetPosition(Vector2.Zero);
            _mask.SetSize(new Vector2(mw, mh));

            // Same proportions as the ColorRect bar: block + Osty buffer, then HP with loss from the right.
            int hp = Math.Max(0, r.CurrentHp);
            int osty = Math.Max(0, r.OstyAbsorb);
            int denom = Math.Max(1, block + osty + hp);
            float blockW = mw * block / denom, ostyW = mw * osty / denom, hpW = mw * hp / denom;
            float hpX = blockW + ostyW;
            float wPerHp = hp > 0 ? hpW / hp : 0f;
            float maxLossW = Math.Clamp(r.MaxLoss, 0, hp) * wPerHp;
            float minLossW = Math.Clamp(r.MinLoss, 0, hp) * wPerHp;
            float fy = -4 * s, fh = mh + 8 * s; // fills overhang the mask by 4px like the scene

            Seg(_block, 0, fy, blockW, fh);
            Seg(_osty, blockW, fy, ostyW, fh);
            Seg(_hp, hpX, fy, hpW - maxLossW, fh);

            // Predicted loss styled like the game's Doom preview; plain red fill if the shader is missing.
            var doom = ForecastStyle.DoomMaterial();
            _lossMax.Material = doom;
            _lossMin.Material = doom;
            _lossMax.SelfModulate = doom != null ? Colors.White : new Color(UiTheme.Red.R, UiTheme.Red.G, UiTheme.Red.B, 0.40f);
            _lossMax.Modulate = doom != null ? new Color(1f, 1f, 1f, TailAlpha) : Colors.White;
            _lossMin.SelfModulate = doom != null ? Colors.White : UiTheme.Red;
            Seg(_lossMax, hpX + hpW - maxLossW, fy, maxLossW, fh);
            Seg(_lossMin, hpX + hpW - minLossW, fy, minLossW, fh);

            // HpLabel: "cur/max" over the bar. Lethal recolors it like NHealthBar does for lethal Doom.
            int fs = Math.Max(8, (int)Math.Round(17 * sc));
            UiTheme.Apply(_hpLabel, fs, r.IsLethal ? ForecastStyle.DoomLethalFont : UiTheme.Cream);
            ForecastStyle.SetFont(_hpLabel, GameAssets.NumericFont);
            ForecastStyle.Outline(_hpLabel, r.IsLethal ? ForecastStyle.DoomLethalOutline : ForecastStyle.HpLabelOutline,
                Math.Max(4, (int)Math.Round(fs * 0.5f)));
            ForecastStyle.Shadow(_hpLabel, sc);
            _hpLabel.Text = $"{r.CurrentHp}/{r.MaxHp}";
            _hpLabel.SetPosition(new Vector2(0, -7 * s));
            _hpLabel.SetSize(new Vector2(w, h + 15 * s));

            // BlockContainer: the shield icon with its number, centered on the bar's left end.
            float icon = h * 1.9f;
            var iconPos = new Vector2(-icon * 0.5f, (h - icon) * 0.5f);
            bool showBlock = block > 0;
            if (_blockIcon != null)
            {
                _blockIcon.Visible = showBlock;
                _blockIcon.SetPosition(iconPos);
                _blockIcon.SetSize(new Vector2(icon, icon));
            }
            _blockLabel.Visible = showBlock && _blockIcon != null;
            if (_blockLabel.Visible)
            {
                int bfs = Math.Max(8, (int)Math.Round(15 * sc));
                UiTheme.Apply(_blockLabel, bfs, UiTheme.Cream);
                ForecastStyle.SetFont(_blockLabel, GameAssets.NumericFont);
                ForecastStyle.Outline(_blockLabel, ForecastStyle.BlockLabelOutline, Math.Max(4, (int)Math.Round(bfs * 0.5f)));
                _blockLabel.Text = block.ToString();
                _blockLabel.SetPosition(iconPos + new Vector2(0, icon * 0.03f));
                _blockLabel.SetSize(new Vector2(icon, icon));
            }
            return true;
        }
        catch { return false; }
    }

    // A NinePatchRect's minimum width is its patch margins, and Godot clamps SetSize to it, so a thin
    // segment would draw 12px wide. Shrink the 6px end caps to fit (the game instead floors widths at 12).
    private static void Seg(NinePatchRect np, float x, float y, float w, float h)
    {
        bool vis = w > 0.5f && h > 0.5f;
        np.Visible = vis;
        if (!vis) return;
        int cap = Math.Clamp((int)(w * 0.5f), 0, 6);
        np.PatchMarginLeft = cap;
        np.PatchMarginRight = cap;
        np.SetPosition(new Vector2(x, y));
        np.SetSize(new Vector2(w, h));
    }
}
