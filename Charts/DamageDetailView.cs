using System;
using System.Collections.Generic;
using Godot;
using STS2_DamageCharts.Common;
using STS2_DamageCharts.Forecast;
using STS2_DamageCharts.Overlay;
using STS2_DamageCharts.Tracking;
using STS2_DamageCharts.Ui;

namespace STS2_DamageCharts.Charts;

// The breakdown view. Two modes:
//  - Interactive (hotkey-toggled): a full-screen takeover — dimmed backdrop, two wide columns
//    (Dealt / Taken by source) over a tall scrollable combat log. Sized to the viewport.
//  - Summary (auto end-of-combat): a compact, click-through panel on the left over the rewards
//    moment.
// Native ScrollContainers can't be used (no engine GUI callbacks reach our CanvasLayer), so the
// log scrolls via a poll-driven draggable scrollbar plus mouse-wheel routed in from the mod's
// input hook (see ScrollBy).
// With native_visuals on (GameAssets), it borrows the game's own look: Kreon fonts (the glyph-spaced
// numeric variant for totals), the hover-tip panel for the summary, the main menu's dark_blur backstop
// for the takeover, the scrollbar art from scenes/ui/scrollbar.tscn, and an icon on every source row.
// Each piece falls back to the original look on its own when its asset is missing.
internal sealed class DamageDetailView
{
    private const float SummaryWidth = 480f;
    private const float SummaryHeight = 540f;
    private const int SummaryMaxRows = 10;
    private const int LogMinRows = 3;       // the summary's log always keeps room for a card with this many lines

    private readonly Node _root;
    private readonly CanvasLayer _layer;
    // Summary/fallback home of _container: mounted beneath the game's hover tips so relic/card/potion tips
    // on the rewards screen draw over the summary panel.
    private readonly OverlayMount _mount;
    private readonly Control _container;
    // Reparent target: in full-screen mode we mount _container into the game's NGlobalUi just beneath
    // the NTopBar node so the top bar floats over us (like the native deck/map). Falls back to _layer
    // (our own CanvasLayer, above everything) when the game nodes aren't available, and for summary mode.
    private Node? _globalUi;
    private Node? _topBar;
    private bool _panelVisible;
    private readonly Control _inputBlock;  // full-screen Stop-filter blocker (full takeover only)
    private readonly ColorRect _dim;      // full-screen dim backdrop (interactive mode only)
    private readonly Panel _bgShadow;     // native hover-tip drop shadow behind _bg (native summary only)
    private readonly Panel _bg;           // themed panel (summary mode)
    private bool _nativePanel;            // _bg/_bgShadow restyled with the game's hover-tip art
    private readonly Control _frameLayer; // the game's panel frames (full view, native_visuals), beneath everything
    private readonly List<(Panel Shadow, Panel Frame)> _frames = new();
    private int _frameCur;
    private StyleBoxTexture? _eFrameStyle, _eShadowStyle;
    private readonly Control _boxLayer;   // rounded panels (full view's sidebar, cards, pill bars), behind bars and text
    private readonly Control _barLayer;   // behind text
    private readonly List<PooledBox> _boxes = new();

    // A pooled rounded panel and the style values last written to it: StyleBoxFlat setters each emit "changed"
    // (theme invalidation and a redraw), so a box only writes what differs from last frame.
    private sealed class PooledBox
    {
        public readonly Panel Panel;
        public readonly StyleBoxFlat Style;
        public (Color Bg, Color Border, int BorderW, int R, bool Left, bool Right)? Last;
        public PooledBox(Panel panel, StyleBoxFlat style) { Panel = panel; Style = style; }
    }
    private readonly List<Label> _labels = new();
    private readonly List<ColorRect> _bars = new();
    private readonly List<TextureRect> _icons = new();
    // Textures by res:// path, revalidated on every hit (LiveCache: the game disposes some of them between rooms).
    private readonly LiveCache<Texture2D> _texCache = new(GodotObject.IsInstanceValid);
    private const float IconSize = 18f;

    // Current layout size (recomputed per render from mode + viewport).
    private float _w = SummaryWidth, _h = SummaryHeight;
    // Font/spacing scale for the full-screen takeover, so text stays legible at high resolutions.
    // (Summary mode keeps a fixed compact size; scale is 1 there.)
    private float _fontScale = 1f;
    // User multiplier from config (ui_scale), applied on top of the automatic resolution scale.
    public float UiScaleMul = 1f;
    // Full-screen takeover: content (text + data bars) renders fully opaque; only the dim backdrop is
    // see-through. Set true while full so the foreground never blends with the game behind it.
    private bool _opaque;
    // Vertical space (px) reserved at the bottom of the full view for the close/back button, so the
    // combat log doesn't render underneath it.
    private float _bottomReserve;

    // Manually-scrolled combat log with a custom, polling-driven scrollbar plus wheel (ScrollBy).
    private readonly ColorRect _track;
    private readonly ColorRect _grabber;
    // Native scrollbar (game art): tinted track body + end caps, and the fixed-size knob. Used instead of
    // _track/_grabber when the textures load. _sbHitL/_sbHitR is the horizontal grab band for UpdateMouse.
    private readonly Panel _nTrack;
    private readonly TextureRect _nCapTop, _nCapBot, _nKnob;
    private StyleBoxTexture? _nTrackStyle, _nTrackStyleSmall, _nTrackApplied;
    private float _sbHitL, _sbHitR;
    // Native backdrop: the game's dark_blur screen-space shader on _dim, eased in on open like NMainMenu.
    private ShaderMaterial? _blurMat;
    private static readonly StringName BlurLod = "lod", BlurMix = "mix_percentage";
    private float _blurT;
    private readonly LiveCache<Texture2D> _nameIconCache = new(GodotObject.IsInstanceValid, 256);
    // Self-drawn close/back button (full-screen only), animated by us from the poll loop. Interaction is
    // poll-based via _closeRect (the native NBackButton's hover/press animations never fire on our layer).
    private readonly Panel _btn;
    private readonly Label _btnGlyph;
    private float _btnShow;                   // animate-in 0→1 (reset when the view opens)
    private float _btnHover;                  // eased hover 0→1
    private float _btnPress;                  // eased press 0→1
    private bool _btnHovered;                 // target: cursor over _closeRect (set in UpdateMouse)
    private bool _btnPressed;                 // target: left held over _closeRect (set in UpdateMouse)
    private Rect2 _closeRect;                 // hit region (local), empty when not shown
    private bool _closeRequested;
    private int _logOffset;
    private bool _logAtBottom = true;
    private bool _grabbing;
    private bool _leftLast;
    private float _grabStartY;
    private int _grabStartOffset;
    private float _trackY, _trackH, _grabberY, _grabberH, _sbX;   // render-state for hit-testing
    private int _maxOffset, _visible;

    // When true, render as an end-of-combat summary (compact, click-through over rewards).
    public bool SummaryMode;
    // The summary panel's outer frame (art included) in viewport pixels, as of the last summary render; empty
    // otherwise. Share fight sits under it.
    public Rect2 SummaryFrame { get; private set; }
    // When true, the full-screen takeover is the run recap (titled "Run Recap"; fight dividers between the
    // fights' round cards). Only meaningful when !SummaryMode.
    public bool RecapMode;
    // Display string for the toggle key, shown in the close button's hover tip (set by the mod from config).
    public string HotkeyHint = "C";

    public DamageDetailView(Node root)
    {
        _root = root;
        _layer = new CanvasLayer { Layer = 129, Name = "Sts2DamageChartsDetail", Visible = false };
        root.AddChild(_layer);

        _mount = new OverlayMount(root, _layer, "Sts2DamageChartsDetailMount");
        _container = new Control { Name = "Detail", MouseFilter = Control.MouseFilterEnum.Ignore };
        _container.SetSize(new Vector2(_w, _h));
        _mount.Holder.AddChild(_container);

        // Transparent full-screen input blocker on our CanvasLayer (above the game and the top bar). In
        // the full-screen takeover it swallows hover/clicks so game elements beneath (stars, energy, …)
        // don't fire tooltips. Our own interaction is poll-based (Input + the wheel hook), so Stop here
        // never interferes with the back button or log scroll. Off in click-through summary mode.
        _inputBlock = new Control { Name = "InputBlock", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        // Because the blocker consumes mouse events, the wheel no longer reaches the mod's unhandled-input
        // hook in full mode — so scroll the log straight from the blocker's own GUI input.
        _inputBlock.GuiInput += OnBlockerGuiInput;
        _layer.AddChild(_inputBlock);

        // Full-screen dim, behind everything (interactive takeover only).
        _dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.82f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _container.AddChild(_dim);

        _bgShadow = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _container.AddChild(_bgShadow);

        _bg = UiTheme.MakePanel(0.86f, 1f);
        _bg.SetPosition(Vector2.Zero);
        _bg.SetSize(new Vector2(_w, _h));
        _container.AddChild(_bg);

        // Game panel frames first, then rounded panels (beneath bars), then bars, then labels and icons on top.
        _frameLayer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _frameLayer.SetPosition(Vector2.Zero);
        _container.AddChild(_frameLayer);
        _boxLayer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _boxLayer.SetPosition(Vector2.Zero);
        _container.AddChild(_boxLayer);
        // Bars go in their own layer added before labels, so text always renders on top.
        _barLayer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _barLayer.SetPosition(Vector2.Zero);
        _barLayer.SetSize(new Vector2(_w, _h));
        _container.AddChild(_barLayer);

        // Custom scrollbar for the log (purely visual; dragged via polling in UpdateMouse).
        _track = new ColorRect { Color = new Color(1f, 1f, 1f, 0.10f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _container.AddChild(_track);
        _grabber = new ColorRect { Color = new Color(UiTheme.Gold.R, UiTheme.Gold.G, UiTheme.Gold.B, 0.65f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _container.AddChild(_grabber);
        _nTrack = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _container.AddChild(_nTrack);
        _nCapTop = MakeSbRect(false);
        _nCapBot = MakeSbRect(false);
        _nCapBot.FlipV = true;                       // scrollbar.tscn: TrackBot is the same edge, flipped
        _nKnob = MakeSbRect(true);

        // Close/back button (full-screen only); clicks detected by polling in UpdateMouse. Added last → on
        // top. Visual only (MouseFilter.Ignore); we animate it ourselves in Render.
        _btn = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        // High-contrast styling so the button reads against the dark takeover backdrop (a dark themed
        // panel blended in): a lighter slate fill with a bright gold border.
        var btnStyle = new StyleBoxFlat { BgColor = new Color(0.20f, 0.23f, 0.32f, 0.96f), BorderColor = UiTheme.Gold };
        btnStyle.SetCornerRadiusAll(8);
        btnStyle.SetBorderWidthAll(3);
        Th.Stylebox(_btn, "panel", btnStyle);
        _container.AddChild(_btn);
        _btnGlyph = UiTheme.MakeLabel(20, UiTheme.Gold);
        _btnGlyph.Text = "←";
        _btnGlyph.HorizontalAlignment = HorizontalAlignment.Center;
        _btnGlyph.VerticalAlignment = VerticalAlignment.Center;
        _btnGlyph.MouseFilter = Control.MouseFilterEnum.Ignore;
        _btnGlyph.Visible = false;
        _container.AddChild(_btnGlyph);
    }

    private TextureRect MakeSbRect(bool knob)
    {
        var r = new TextureRect
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            // The game draws its knob at a fixed size, keep-aspect, centered; caps stretch to their box.
            StretchMode = knob ? TextureRect.StretchModeEnum.KeepAspectCentered : TextureRect.StretchModeEnum.Scale,
            Visible = false,
        };
        if (!knob) r.SelfModulate = GameAssets.ScrollTrackTint;
        _container.AddChild(r);
        return r;
    }

    // The full-screen blocker eats the wheel (it's MouseFilter.Stop), so scroll the log from here.
    private void OnBlockerGuiInput(InputEvent e)
    {
        try
        {
            if (e is InputEventMouseButton mb && mb.Pressed)
            {
                if (mb.ButtonIndex == MouseButton.WheelUp) { ScrollBy(-3); _inputBlock.AcceptEvent(); }
                else if (mb.ButtonIndex == MouseButton.WheelDown) { ScrollBy(3); _inputBlock.AcceptEvent(); }
            }
            else if (e is InputEventPanGesture pg) { ScrollByPan(pg.Delta.Y); _inputBlock.AcceptEvent(); }
        }
        catch { }
    }

    // macOS trackpads send two-finger scrolling as InputEventPanGesture, never as wheel buttons. Mirror
    // Godot's ScrollContainer (one unit of pan delta = 1/8 page) and carry the fractional remainder.
    private float _panAccum;
    public void ScrollByPan(float deltaY)
    {
        _panAccum += deltaY * Math.Max(1, _visible) / 8f;
        int rows = (int)_panAccum;
        if (rows == 0) return;
        _panAccum -= rows;
        ScrollBy(rows);
    }

    // Consumed by the mod: true once after a click on the close button.
    public bool TakeCloseRequest() { var c = _closeRequested; _closeRequested = false; return c; }

    // Scroll the log by a number of display rows (negative = toward older lines). Driven by the
    // mouse wheel via the mod's input hook; clamped on the next RenderLog.
    public void ScrollBy(int deltaRows)
    {
        if (deltaRows == 0) return;
        _logOffset = Math.Max(0, _logOffset + deltaRows);
        _logAtBottom = false;
    }

    // Poll-based scrollbar drag / track paging (called from the mod's tick while interactive).
    public void UpdateMouse(bool leftDown)
    {
        if (!IsValid() || !_panelVisible) return;
        try
        {
            Vector2 m = _container.GetLocalMousePosition();
            bool overClose = _closeRect.Size.X > 0f && _closeRect.HasPoint(m);
            _btnHovered = overClose;
            _btnPressed = overClose && leftDown;
            if (leftDown && !_leftLast && overClose) _closeRequested = true;
            // The scrollbar is draggable in summary mode too: the summary's input catcher covers the panel,
            // so a click on the track never reaches the rewards screen underneath.
            bool overTrackX = m.X >= _sbHitL && m.X <= _sbHitR;
            bool overGrabber = overTrackX && m.Y >= _grabberY && m.Y <= _grabberY + _grabberH;
            bool overTrack = overTrackX && m.Y >= _trackY && m.Y <= _trackY + _trackH;

            if (leftDown && !_leftLast)
            {
                if (overGrabber) { _grabbing = true; _grabStartY = m.Y; _grabStartOffset = _logOffset; }
                else if (overTrack && _maxOffset > 0) { _logOffset += (m.Y < _grabberY ? -_visible : _visible); _logAtBottom = false; }
            }
            else if (leftDown && _grabbing && _maxOffset > 0)
            {
                float denom = Math.Max(1f, _trackH - _grabberH);
                _logOffset = _grabStartOffset + (int)Math.Round((m.Y - _grabStartY) / denom * _maxOffset);
                _logAtBottom = false;
            }
            else if (!leftDown) _grabbing = false;

            _logOffset = Math.Clamp(_logOffset, 0, _maxOffset);
            _logAtBottom = _logOffset >= _maxOffset;
            _leftLast = leftDown;
        }
        catch { }
    }

    // _container can be freed by the game if it was reparented under NGlobalUi (or the mount's holder
    // beneath the hover tips) and the run ends, so validity requires the layer, holder and container.
    public bool IsValid() => _mount.IsValid && GodotObject.IsInstanceValid(_container);
    public bool Visible => IsValid() && _panelVisible;
    // Cursor over the close/back button (summary ✕ or full-screen back), as of the last UpdateMouse.
    public bool CloseHovered => Visible && _btnHovered && _closeRect.Size.X > 0f;
    public void SetVisible(bool v)
    {
        if (v && !_panelVisible) { _btnShow = 0f; _btnHover = 0f; _btnPress = 0f; _btnHovered = false; _btnPressed = false; _blurT = 0f; }
        _panelVisible = v;
        if (!IsValid()) return;
        _layer.Visible = v;   // the summary/full input catcher lives directly on the layer
        _mount.Visible = v;
        try { _container.Visible = v; } catch { }
    }
    public void Dispose()
    {
        try { if (GodotObject.IsInstanceValid(_container) && _container.GetParent() != _mount.Holder) _container.GetParent()?.RemoveChild(_container); } catch { }
        try { if (GodotObject.IsInstanceValid(_container)) _container.QueueFree(); } catch { }
        _mount.Dispose();
    }

    // Mount _container beneath the game's TopBar (full mode) or in the overlay mount (summary / fallback),
    // which itself sits beneath the game's hover tips. Guarded so a missing/stale game node can never
    // disrupt rendering or combat.
    private void EnsurePlacement(bool full)
    {
        try
        {
            if (full && ResolveGameNodes())
            {
                if (_container.GetParent() != _globalUi)
                {
                    _container.GetParent()?.RemoveChild(_container);
                    _globalUi!.AddChild(_container);
                }
                // Order immediately before the top bar (later siblings draw on top).
                var kids = _globalUi!.GetChildren();
                int ci = kids.IndexOf(_container), ti = kids.IndexOf((Node)_topBar!);
                if (ci >= 0 && ti >= 0 && ci + 1 != ti)
                    _globalUi.MoveChild(_container, ci < ti ? ti - 1 : ti);
                _container.Visible = _panelVisible;
                return;
            }

            if (_container.GetParent() != _mount.Holder)
            {
                _container.GetParent()?.RemoveChild(_container);
                _mount.Holder.AddChild(_container);
            }
            _mount.Place();
            _container.Visible = _panelVisible;
        }
        catch { /* leave placement as-is on any error */ }
    }

    private bool ResolveGameNodes()
    {
        if (_globalUi != null && GodotObject.IsInstanceValid(_globalUi) && _topBar != null && GodotObject.IsInstanceValid(_topBar))
            return true;
        try
        {
            _globalUi = FindByType(_root, "NGlobalUi");
            _topBar = _globalUi != null ? FindByType(_globalUi, "NTopBar") : null;
        }
        catch { _globalUi = null; _topBar = null; }
        return _globalUi != null && _topBar != null;
    }

    private static Node? FindByType(Node n, string typeName)
    {
        try
        {
            if (n.GetType().Name == typeName) return n;
            foreach (var ch in n.GetChildren()) { var r = FindByType(ch, typeName); if (r != null) return r; }
        }
        catch { }
        return null;
    }

    // Summary log metrics, shared by Render's stacking math and RenderSummaryLog.
    private const float LogHeadH = 22f;     // "Combat Log" heading -> first line
    private float LogListBottom => _h - 10f;

    // Top padding (viewport px) that pushes full-screen content below the game's top bar (~8% of
    // viewport height, letterbox-excluded) while the dim backdrop stays full-bleed. 9% leaves a hair.
    private static float TopBarInset(Vector2 vp) => Math.Max(48f, vp.Y * 0.09f);

    public void Render(SourceSnapshot snap, Color[] palette)
    {
        if (!IsValid() || !_panelVisible) return;
        try
        {
            bool full = !SummaryMode;
            EnsurePlacement(full);
            Vector2 vp;
            try { vp = _container.GetViewportRect().Size; } catch { vp = new Vector2(1920f, 1080f); }

            // Full-screen takeover covers the whole viewport (dim backdrop included); the content is
            // padded down by the top bar's height so the header clears it while the backdrop stays
            // full-bleed. When mounted beneath NTopBar the bar also draws over the padded strip.
            float top = full ? TopBarInset(vp) : 0f;
            _fontScale = (full ? UiTheme.Scale(vp) : 1f) * Math.Clamp(UiScaleMul, 0.5f, 4f);
            // Full view: fit layout E to the width (RoundCards.Frame); on narrow aspect ratios or a high ui_scale
            // the whole view scales down so the log's text column keeps its minimum.
            _fullFrame = full ? RoundCards.Frame(vp.X, 1.5f * _fontScale) : default;
            if (full) _fontScale = _fullFrame.U / 1.5f;
            // The summary and the full view scroll in different units: start each at the newest lines.
            if (full != _lastFull) { _logOffset = 0; _logAtBottom = true; _lastFull = full; }
            _opaque = full;
            _bottomReserve = 0f;

            // ----- Layout frame -----
            if (full)
            {
                _w = vp.X; _h = vp.Y;
                _container.SetPosition(Vector2.Zero);
            }
            else
            {
                _w = SummaryWidth; _h = SummaryHeight;
                _container.SetPosition(new Vector2(40f, Math.Max(10f, (vp.Y - _h) * 0.5f)));
            }
            _container.SetSize(new Vector2(_w, _h));
            _dim.Visible = full;
            if (full) { _dim.SetPosition(Vector2.Zero); _dim.SetSize(new Vector2(_w, _h)); UpdateBlur(); }
            // Input-catcher: full-screen under the takeover (suppresses stray game tooltips), or just over
            // the summary panel (so the mouse wheel scrolls the summary log instead of the rewards screen
            // underneath eating it). Outside the summary panel stays click-through.
            _inputBlock.Visible = full || SummaryMode;
            if (full) { _inputBlock.SetPosition(Vector2.Zero); _inputBlock.SetSize(vp); }
            else if (SummaryMode) { _inputBlock.SetPosition(_container.Position); _inputBlock.SetSize(new Vector2(_w, _h)); }
            _bg.Visible = !full;
            if (!full) { _bg.SetPosition(Vector2.Zero); _bg.SetSize(new Vector2(_w, _h)); }
            _bgShadow.Visible = false;
            SummaryFrame = full ? new Rect2() : new Rect2(_container.Position, new Vector2(_w, _h));
            if (!full && EnsureNativePanel())
            {
                // The hover-tip frame's art sits in its outer margins (content inset L22 T16 R45 B28), so
                // grow the frame outward around the unchanged content layout instead of shifting the text.
                var r = new Rect2(-12f, -8f, _w + 47f, _h + 26f);
                _bg.SetPosition(r.Position); _bg.SetSize(r.Size);
                _bgShadow.Visible = true; _bgShadow.SetPosition(r.Position); _bgShadow.SetSize(r.Size);
                SummaryFrame = new Rect2(_container.Position + r.Position, r.Size);
            }
            _barLayer.SetSize(new Vector2(_w, _h));
            _boxLayer.SetSize(new Vector2(_w, _h));

            // Full view: layout E's 40 px side padding (mockup px x 1.5 at 1080p, times the UI scale).
            float pad = full ? _fullFrame.Pad : 14f;
            float gap = full ? pad : 12f;
            float headerY = full ? top + pad * 0.6f : 8f;
            bool recap = full && RecapMode;
            float colW = (_w - pad * 2f - gap) * 0.5f;
            float leftX = pad;
            float rightX = pad + colW + gap;
            float rowH = full ? 26f * _fontScale : 22f;

            // Close/back button, full-screen only: a self-drawn button (bottom-left, like deck/map) that we
            // animate ourselves — fades + scales in on open, grows/brightens on hover, depresses on press.
            // The click is detected by polling _closeRect in UpdateMouse.
            if (full)
            {
                float bw = 120f * _fontScale, bh = 84f * _fontScale;
                float bx = pad, by = _h - bh - pad * 0.5f;
                _closeRect = new Rect2(bx, by, bw, bh);
                _bottomReserve = bh + pad;                    // distance from screen bottom to keep the sidebar clear of the button

                // Ease the three animation scalars toward their targets (~framerate-independent enough).
                const float k = 0.25f;
                _btnShow += (1f - _btnShow) * k;
                _btnHover += ((_btnHovered ? 1f : 0f) - _btnHover) * k;
                _btnPress += ((_btnPressed ? 1f : 0f) - _btnPress) * k;

                float scale = (0.85f + 0.15f * _btnShow) * (1f + 0.08f * _btnHover - 0.05f * _btnPress);
                float bright = 0.95f + 0.25f * _btnHover - 0.12f * _btnPress;
                var mod = new Color(bright, bright, bright, _btnShow);
                var piv = new Vector2(bw * 0.5f, bh * 0.5f);

                _btn.Visible = true;
                _btn.SetPosition(new Vector2(bx, by)); _btn.SetSize(new Vector2(bw, bh));
                _btn.PivotOffset = piv; _btn.Scale = new Vector2(scale, scale); _btn.Modulate = mod;

                UiTheme.Apply(_btnGlyph, Math.Max(12, (int)Math.Round(30f * _fontScale)), UiTheme.Gold);
                _btnGlyph.Text = "←";
                _btnGlyph.Visible = true;
                _btnGlyph.SetPosition(new Vector2(bx, by)); _btnGlyph.SetSize(new Vector2(bw, bh));
                _btnGlyph.PivotOffset = piv; _btnGlyph.Scale = new Vector2(scale, scale); _btnGlyph.Modulate = mod;
            }
            else if (SummaryMode)
            {
                // Compact ✕ in the summary's top-right corner — mouse dismiss (click polled via _closeRect).
                float bs = 26f, bx = _w - bs - 10f, by = 6f;
                _closeRect = new Rect2(bx, by, bs, bs);
                _btn.Visible = false;
                UiTheme.Apply(_btnGlyph, 18, UiTheme.Cream);
                _btnGlyph.Text = "✕";
                _btnGlyph.Visible = true;
                _btnGlyph.PivotOffset = Vector2.Zero; _btnGlyph.Scale = Vector2.One; _btnGlyph.Modulate = Colors.White;
                _btnGlyph.SetPosition(new Vector2(bx, by)); _btnGlyph.SetSize(new Vector2(bs, bs));
            }
            else { _btn.Visible = false; _btnGlyph.Visible = false; _closeRect = new Rect2(); }

            int slot = Math.Min(snap.LocalSlot, snap.PlayerCount - 1);
            if (slot < 0) slot = 0;
            var ps = (slot < snap.PerPlayer.Length) ? snap.PerPlayer[slot] : new PlayerSources();
            var color = palette[Math.Min(slot, palette.Length - 1)];
            string who = (snap.PlayerCount > 1 && slot < snap.Labels.Length) ? $" – {snap.Labels[slot]}" : "";
            int cur = 0, barCur = 0, iconCur = 0, boxCur = 0;
            _frameCur = 0;

            if (full)
            {
                RenderFull(ref cur, ref barCur, ref iconCur, ref boxCur, snap, ps, palette, top, pad,
                    (recap ? "Run Recap" : "Combat Stats") + who);
            }
            else
            {
                // ----- Summary: header, the Dealt/Taken columns, the potions row, then the log -----
                // Source rows are capped so the log keeps LogMinRows.
                float vgap = 12f;
                float colTop = headerY + 32f;
                float colHeadH = 24f;                                     // column heading -> first row (RenderColumn)
                float logMinH = LogHeadH + RoundCards.CardHeight(LogMinRows, CardMetrics.Scaled(SummaryU));
                float potH = ps.Potions.Count > 0 ? 24f : 0f;          // potions used: one row under the columns
                float colsLimit = LogListBottom - potH - vgap - logMinH;
                int srcCount = Math.Max(1, Math.Max(ps.Dealt.Count, ps.Taken.Count));   // empty column = one "—" row
                int fitRows = (int)((colsLimit - (colTop + colHeadH)) / rowH);
                int rowCap = Math.Min(fitRows, SummaryMaxRows);
                // Overflowing columns spend their last line on "+ N more".
                int maxRows = Math.Max(1, srcCount <= rowCap ? rowCap : rowCap - 1);
                int colLines = Math.Min(srcCount, maxRows) + (srcCount > maxRows ? 1 : 0);
                float colBottom = colTop + colHeadH + colLines * rowH;
                float potY = colBottom + (potH > 0f ? vgap * 0.5f : 0f);
                if (potH > 0f) colBottom = potY + potH;

                // The close key lives in the close button's hover tip ("Close (C)"), so the header is just the
                // title, shrunk to fit beside the corner close button.
                float headerMaxW = (_closeRect.Size.X > 0f ? _closeRect.Position.X - 6f : _w - 10f) - 10f;
                var header = Next(ref cur, 16, UiTheme.Gold, FontRole.Bold, "Combat Summary" + who, headerMaxW);
                header.SetPosition(new Vector2(10f, headerY));

                RenderColumn(ref cur, ref barCur, ref iconCur, leftX, colTop, colW, rowH, maxRows, false, "DEALT", ps.Dealt, ps.DealtTotal, color, false);
                RenderColumn(ref cur, ref barCur, ref iconCur, rightX, colTop, colW, rowH, maxRows, false, "TAKEN", ps.Taken, ps.TakenTotal, ATaken, true);
                if (potH > 0f) RenderPotions(ref cur, ref iconCur, leftX, potY, _w - pad * 2f, ps.Potions, false);
                RenderSummaryLog(ref cur, ref barCur, ref iconCur, ref boxCur, snap, palette, colBottom + vgap);
            }

            for (int i = cur; i < _labels.Count; i++) _labels[i].Visible = false;
            for (int i = barCur; i < _bars.Count; i++) _bars[i].Visible = false;
            for (int i = iconCur; i < _icons.Count; i++) _icons[i].Visible = false;
            for (int i = boxCur; i < _boxes.Count; i++) _boxes[i].Panel.Visible = false;
            for (int i = _frameCur; i < _frames.Count; i++) { _frames[i].Frame.Visible = false; _frames[i].Shadow.Visible = false; }
        }
        catch (Exception ex)
        {
            // A frame that throws part-way would leave every pooled node it hadn't reached yet showing the last
            // good frame, which may be another fight's summary: hide them all, so the panel is blank, not wrong.
            HidePooled();
            ErrorLog.Once("detail render", ex);
        }
    }

    private void HidePooled()
    {
        try
        {
            foreach (var l in _labels) l.Visible = false;
            foreach (var b in _bars) b.Visible = false;
            foreach (var i in _icons) i.Visible = false;
            foreach (var b in _boxes) b.Panel.Visible = false;
            foreach (var f in _frames) { f.Frame.Visible = false; f.Shadow.Visible = false; }
        }
        catch { }
    }

    // ===== Full view: layout E (sidebar + round cards) =====
    // Mockup colors (sts2-saves layouts "Combined"), shared by the sidebar tiles, source bars and card rows.
    private static readonly Color EPanel = new Color("12222a"), EBorder = new Color("24363d"), ERowLine = new Color("1b2c33");
    private static readonly Color ETrack = new Color("1d2f36"), EBarTrack = new Color("172328"), EDot = new Color("6b7a80");
    private static readonly Color EDealt = new Color("7fc4ef"), ETaken = new Color("ef6a62");
    // Amount text in the kind's color, shared by the summary and the full view (LogPalette: >= 4.5:1 on the panels).
    private static readonly Color ATaken = new Color(LogPalette.Taken), ASaved = new Color(LogPalette.Saved),
        AHealed = new Color(LogPalette.Healed), ADealt = new Color(LogPalette.Dealt);
    private static readonly Color ESavedBar = new Color("6fb7ff"), EHealedBar = new Color("6cc36a"), ECream = new Color("efe6cf");
    private static readonly (string Label, Color Bg, Color Caption, Color Value)[] ETiles =
    {
        ("Dealt", new Color("172a32"), new Color("9fb6bf"), new Color("7fc4ef")),
        ("Taken", new Color("2a1719"), new Color("c9a3a0"), new Color(LogPalette.Taken)),
        ("Saved", new Color("15233a"), new Color("a3b6d0"), new Color(LogPalette.Saved)),
        ("Healed", new Color("162a1c"), new Color("a3c6a8"), new Color(LogPalette.Healed)),
    };
    // Native look (native_visuals): the game's hover-tip frame for panels, its title shadow, the scrollbar track's
    // tint for dividers and the HP bar track's dark inset for bar tracks.
    private const string ETopBarHeart = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_heart.tres";
    private const string EBufferIcon = "res://images/atlases/power_atlas.sprites/buffer_power.tres";
    private static readonly Color ETextShadow = new(0f, 0f, 0f, 0.25098f);       // hover_tip.tscn title shadow
    private static readonly Color EHpTrack = new(0f, 0f, 0f, 0.55f);             // health bar track (as the forecast's)
    private bool _eNative;
    private FullFrame _fullFrame;
    private bool _lastFull;
    private readonly List<CardItem> _cards = new();
    private static readonly Color EMuted = new Color("9fb6bf");
    private readonly long[] _tileValues = new long[4];
    private (long, long, string)? _blockText;

    private string BlockText(long block, long absorbed)
    {
        if (_blockText is { } b && b.Item1 == block && b.Item2 == absorbed) return b.Item3;
        string t = absorbed > 0 ? $"Block {block}  ·  Absorbed {absorbed}" : $"Block {block}";
        _blockText = (block, absorbed, t);
        return t;
    }
    private readonly Dictionary<(long, int), string> _countTexts = new();   // "12 dealt" / "4 taken" / tile values

    private string CountText(long n, int kind)
    {
        if (_countTexts.TryGetValue((n, kind), out var t)) return t;
        if (_countTexts.Count > 4000) _countTexts.Clear();
        t = kind switch { 0 => $"{n} dealt", 1 => $"{n} taken", 3 => $"+ {n} more", _ => n.ToString() };
        _countTexts[(n, kind)] = t;
        return t;
    }

    private void RenderFull(ref int cur, ref int barCur, ref int iconCur, ref int boxCur, SourceSnapshot snap,
                            PlayerSources ps, Color[] palette, float top, float pad, string title)
    {
        var f = _fullFrame;
        float u = f.U;                             // one mockup px (1280x720) at this viewport and UI scale
        _eNative = EnsureFrameStyles();
        float sideX = pad, sideY = top + 14f * u, sideW = f.SideW;
        float sideBottom = Math.Max(sideY + 120f * u, _h - _bottomReserve);
        RenderSidebar(ref cur, ref barCur, ref iconCur, ref boxCur, snap, ps, sideX, sideY, sideW, sideBottom - sideY, u, title);

        // Right: the "Combat Log" heading, then the round cards, scrolling as one list.
        float logX = f.LogX;
        float logRight = f.LogX + f.LogW;
        var head = Next(ref cur, 33, UiTheme.Gold, FontRole.Bold, shadow: _eNative);
        head.Text = "Combat Log";
        head.SetPosition(new Vector2(logX, sideY));
        float viewTop = sideY + 30f * u + 14f * u;
        float viewH = Math.Max(40f * u, _h - 28f * u - viewTop);
        RenderCards(ref cur, ref barCur, ref iconCur, ref boxCur, snap.Log, palette, snap.PlayerCount > 1,
            logX, logRight - logX, viewTop, viewH, u, f.BarW);
        RenderScrollbar(true, viewTop, viewH, logRight + pad * 0.4f, 8f, _visible + _maxOffset);
    }

    // The hover-tip frame and shadow styles, made once (shared by every frame panel). False when native_visuals
    // is off or the art is missing: panels then draw as plain rounded boxes.
    private bool EnsureFrameStyles()
    {
        if (!GameAssets.Enabled) return false;
        try
        {
            _eFrameStyle ??= GameAssets.NativePanel();
            _eShadowStyle ??= GameAssets.NativePanelShadow();
        }
        catch { }
        return _eFrameStyle != null;
    }

    // A panel behind content: the game's hover-tip frame (with its shadow) sized so its VISIBLE box is the rect
    // (NativeLook: 9-slice margins are texture px, so the node is drawn at 1080p size and scaled), else a plain
    // rounded box in the mockup's colors.
    private void PanelBg(ref int boxCur, float x, float y, float w, float h, float u)
    {
        if (w <= 0f || h <= 0f) return;
        // The hover-tip 9-slice needs ~75 texture px of height (its top and bottom margins); a card sliver cut by the
        // scroll edge would squish it, so short panels stay plain boxes.
        if (!_eNative || h < 75f * _fontScale) { Box(ref boxCur, x, y, w, h, EPanel, EBorder, 1f, 6f * u); return; }
        (Panel Shadow, Panel Frame) f;
        if (_frameCur < _frames.Count) f = _frames[_frameCur];
        else
        {
            f = (NativeLook.MakeFramePanel(), NativeLook.MakeFramePanel());
            _frameLayer.AddChild(f.Shadow);
            _frameLayer.AddChild(f.Frame);
            _frames.Add(f);
        }
        _frameCur++;
        float sc = Math.Max(0.1f, _fontScale);
        var size = new Vector2(w / sc + NativeLook.OverhangX, h / sc + NativeLook.OverhangY);
        Th.Stylebox(f.Frame, "panel", _eFrameStyle!);
        if (_eShadowStyle != null) Th.Stylebox(f.Shadow, "panel", _eShadowStyle);
        Place(f.Frame, x, y, size, sc);
        Place(f.Shadow, x, y, size, sc);
        f.Shadow.Visible = _eShadowStyle != null;

        static void Place(Panel p, float x, float y, Vector2 size, float sc)
        {
            p.Scale = new Vector2(sc, sc);
            p.SetPosition(new Vector2(x, y));
            p.SetSize(size);
            p.Visible = true;
        }
    }

    private void RenderSidebar(ref int cur, ref int barCur, ref int iconCur, ref int boxCur, SourceSnapshot snap,
                               PlayerSources ps, float x, float y, float w, float h, float u, string title)
    {
        PanelBg(ref boxCur, x, y, w, h, u);
        // Inner margins: the hover-tip text insets (L22 T16 R26 B17 at 1080p) on the game's frame, else the mockup's 18.
        float pad = _eNative ? 24f * _fontScale : 18f * u, gap = 16f * u;
        float ix = x + pad, iw = w - 2f * pad, cy = y + pad;
        float bottom = y + h - pad;

        var t = Next(ref cur, 36, UiTheme.Gold, FontRole.Bold, title, iw, shadow: _eNative);
        t.SetPosition(new Vector2(ix, cy));
        cy += 34f * u + gap;

        // Room planning: every top source should show. When they don't fit at the minimum row pitch, the tiles
        // get compact (74 -> 58) and that space goes to the sources. The Block line shows only when non-zero.
        bool blockLine = ps.BlockTotal > 0 || ps.AbsorbedTotal > 0;
        float potH = ps.Potions.Count > 0 ? 30f * u : 0f;
        float tg = 10f * u, tw = (iw - tg) * 0.5f;
        float rowH = 27f * u, maxPitch = 28f * u + gap, minPitch = 29f * u;   // name line 21 + 6 px bar
        int nSources = ps.Dealt.Count + ps.Taken.Count;
        float SourcesRoom(float tileH) => bottom - potH - (cy + 2f * tileH + tg + 8f * u + (blockLine ? 22f * u + gap : 0f) + 22f * u + gap * 0.75f);
        float th = SourcesRoom(74f * u) >= rowH + Math.Max(0, nSources - 1) * minPitch ? 74f * u : 58f * u;
        bool compact = th < 74f * u;

        // 2x2 totals: Dealt, Taken, Saved (Buffer), Healed; always all four (0 when none) so the grid is stable.
        _tileValues[0] = ps.DealtTotal; _tileValues[1] = ps.TakenTotal; _tileValues[2] = ps.BufferedTotal; _tileValues[3] = ps.HealTotal;
        for (int i = 0; i < 4; i++)
        {
            float tx = ix + (i % 2) * (tw + tg), ty = cy + (i / 2) * (th + tg);
            var tile = ETiles[i];
            Box(ref boxCur, tx, ty, tw, th, tile.Bg, default, 0f, 4f * u);
            // Native: the game's icon for the stat at the tile's left; caption and number centered beside it.
            float textX = tx, textW = tw;
            var tileTex = _eNative ? TileIcon(i) : null;
            if (tileTex != null)
            {
                float isz = Math.Min(32f * u, th - 16f * u);
                var ic = NextIcon(ref iconCur);
                ic.Texture = tileTex;
                ic.SetPosition(new Vector2(tx + 8f * u, ty + (th - isz) * 0.5f));
                ic.SetSize(new Vector2(isz, isz));
                textX = tx + 8f * u + isz;
                textW = tw - (textX - tx);
            }
            var cap = Next(ref cur, 19, tile.Caption);
            cap.Text = tile.Label;
            cap.HorizontalAlignment = HorizontalAlignment.Center;
            cap.SetPosition(new Vector2(textX, ty + (compact ? 4f : 8f) * u)); cap.SetSize(new Vector2(textW, 0f));
            // Numbers in the game's numeric Kreon with a heavy dark outline and soft shadow, like the top bar's.
            var val = Next(ref cur, compact ? 36 : 45, tile.Value, FontRole.Numeric, CountText(_tileValues[i], 2), textW,
                outline: _eNative ? Math.Max(2, (int)Math.Round(10f * _fontScale)) : null, shadow: _eNative);
            val.HorizontalAlignment = HorizontalAlignment.Center;
            val.SetPosition(new Vector2(textX, ty + (compact ? 20f : 26f) * u)); val.SetSize(new Vector2(textW, 0f));
        }
        cy += 2f * th + tg + 8f * u;

        // Block (and Absorbed, when a pet took hits) as one quiet line under the tiles.
        if (blockLine)
        {
            var extra = Next(ref cur, 19, EMuted);   // muted: the full view forces text opaque
            extra.Text = BlockText(ps.BlockTotal, ps.AbsorbedTotal);
            extra.SetPosition(new Vector2(ix, cy));
            cy += 22f * u + gap;
        }

        // Top sources: dealt then taken, name + amount over a 6 px bar, sharing the room left (RoundCards.SidebarPlan:
        // a "+ N more" row when they don't all fit). Potions at the foot give way first; with no room for a row the
        // heading is left out too.
        float headH = 22f * u + gap * 0.75f;
        var plan = RoundCards.SidebarPlan(bottom - potH - cy - headH, ps.Dealt.Count, ps.Taken.Count, rowH, maxPitch, minPitch);
        if (plan.More > 0 && potH > 0f)
        {
            potH = 0f;
            plan = RoundCards.SidebarPlan(bottom - cy - headH, ps.Dealt.Count, ps.Taken.Count, rowH, maxPitch, minPitch);
        }
        if (potH > 0f) RenderPotions(ref cur, ref iconCur, ix, bottom - potH + 4f * u, iw, ps.Potions, true);
        var (nd, nt, more, pitch) = plan;
        if (nd + nt == 0) return;
        var sh = Next(ref cur, 22, UiTheme.Gold, FontRole.Bold);
        sh.Text = "Top sources";
        sh.SetPosition(new Vector2(ix, cy));
        cy += headH;
        long max = 1;
        for (int i = 0; i < nd; i++) max = Math.Max(max, ps.Dealt[i].Total);
        for (int i = 0; i < nt; i++) max = Math.Max(max, ps.Taken[i].Total);
        for (int i = 0; i < nd + nt; i++)
        {
            bool taken = i >= nd;
            var e = taken ? ps.Taken[i - nd] : ps.Dealt[i];
            Color c = taken ? ETaken : EDealt;
            Color ac = taken ? ATaken : EDealt;   // the amount text: the accessible taken red
            float nameX = ix;
            var tex = e.Icon != null ? LoadTex(e.Icon) : null;
            tex ??= FallbackIcon(e.Name, e.Total, taken);
            if (tex != null)
            {
                var ic = NextIcon(ref iconCur);
                ic.Texture = tex;
                ic.SetPosition(new Vector2(ix, cy));
                ic.SetSize(new Vector2(20f * u, 20f * u));
                nameX += 24f * u;
            }
            var amt = Next(ref cur, 21, ac, FontRole.Numeric);
            amt.Text = CountText(e.Total, 2);
            amt.HorizontalAlignment = HorizontalAlignment.Right;
            amt.SetPosition(new Vector2(ix, cy)); amt.SetSize(new Vector2(iw, 0f));
            var name = Next(ref cur, 21, ECream, FontRole.Body, e.Name, Math.Max(0f, iw - (nameX - ix) - 60f * u));
            name.SetPosition(new Vector2(nameX, cy));
            float by = cy + 21f * u;
            Box(ref boxCur, ix, by, iw, 6f * u, _eNative ? EHpTrack : ETrack, default, 0f, 3f * u);
            float bw = LogLayout.BarWidth(e.Total, max, iw, 6f * u);
            if (bw > 0f) Box(ref boxCur, ix, by, bw, 6f * u, c, default, 0f, 3f * u);
            cy += pitch;
        }
        if (more > 0)
        {
            var m = Next(ref cur, 21, EMuted);
            m.Text = CountText(more, 3);
            m.SetPosition(new Vector2(ix, cy));
        }
    }

    // The tiles' game icons: Dealt the attack intent (sword), Taken the top bar's heart, Saved the Buffer power,
    // Healed the heal intent. Cached by LoadTex / GameAssets; null keeps the tile text-only.
    private Texture2D? TileIcon(int i)
    {
        try
        {
            return i switch
            {
                0 => GameAssets.IntentAttackIcon(10),
                1 => LoadTex(ETopBarHeart),
                2 => LoadTex(EBufferIcon),
                _ => HealIcon(),
            };
        }
        catch { return null; }
    }

    // The log as round cards (RoundCards), scrolled by _logOffset steps; only what lies inside the view is drawn
    // (a card cut by the edge keeps the visible part of its panel and whole rows only).
    private void RenderCards(ref int cur, ref int barCur, ref int iconCur, ref int boxCur, LogEntry[] log, Color[] palette,
                             bool coop, float x, float w, float viewTop, float viewH, float u, float barW)
    {
        var m = CardMetrics.Scaled(u);
        // On the game's frame the content sits at the hover tips' text insets instead of the mockup's padding.
        if (_eNative) m = m with { PadX = 24f * _fontScale, PadY = 17f * _fontScale };
        Color line = _eNative ? GameAssets.ScrollTrackTint : ERowLine;
        Color barTrack = _eNative ? EHpTrack : EBarTrack;
        Color splitTrack = _eNative ? GameAssets.ScrollTrackTint : ETrack;
        float contentH = RoundCards.Build(log, m, _cards);
        _visible = RoundCards.VisibleSteps(viewH, m);
        _maxOffset = RoundCards.MaxOffset(contentH, viewH, m);
        if (_logAtBottom) _logOffset = _maxOffset;   // follow newest, also once the log window is full and slides
        _logOffset = Math.Clamp(_logOffset, 0, _maxOffset);
        _logAtBottom = _logOffset >= _maxOffset;
        float scroll = RoundCards.ScrollY(_logOffset, contentH, viewH, m);
        if (log.Length == 0)
        {
            var none = Next(ref cur, 22, EMuted);
            none.Text = "No hits yet";
            none.SetPosition(new Vector2(x + m.PadX, viewTop));
            return;
        }
        float viewBottom = viewTop + viewH;
        long maxAmount = LogLayout.MaxAmount(log);
        bool oneEnemy = LogText.SingleDealtTarget(log);
        var cols = RoundCards.Columns(x + m.PadX, w - 2f * m.PadX, u, barW);
        float iconSz = 26f * u;

        foreach (var item in _cards)
        {
            float y0 = viewTop + item.Y - scroll, y1 = y0 + item.H;
            if (y1 <= viewTop || y0 >= viewBottom) continue;
            if (item.Kind == CardItemKind.FightDivider)
            {
                if (y0 < viewTop || y1 > viewBottom) continue;
                var lbl = Next(ref cur, 30, UiTheme.Gold, FontRole.Bold, shadow: _eNative);
                lbl.Text = log[item.First].Text;
                var d = LogLayout.Divider(x, x + w, MeasureLabel(lbl), 12f * u);
                lbl.SetPosition(new Vector2(d.LabelX, y0 + (item.H - 38f * u) * 0.5f));
                float ly = y0 + item.H * 0.5f - 1.5f * u;
                DividerLine(ref barCur, d.LeftLineX, d.LeftLineW, ly, 2f * u, UiTheme.Gold);
                DividerLine(ref barCur, d.RightLineX, d.RightLineW, ly, 2f * u, UiTheme.Gold);
                continue;
            }

            // Panel: the visible part of the card.
            float py0 = Math.Max(y0, viewTop), py1 = Math.Min(y1, viewBottom);
            PanelBg(ref boxCur, x, py0, w, py1 - py0, u);

            // Header: "Round N" left, "<d> dealt · <t> taken" right.
            float hy = y0 + m.PadY;
            if (hy >= viewTop && hy + m.HeaderH <= viewBottom)
            {
                var rl = Next(ref cur, 30, UiTheme.Gold, FontRole.Bold, shadow: _eNative);
                rl.Text = RoundLabel(item.Number);
                rl.SetPosition(new Vector2(x + m.PadX, hy - 4f * u));
                float right = x + w - m.PadX;
                var tk = Next(ref cur, 22, ATaken);
                tk.Text = CountText(item.Taken, 1);
                tk.HorizontalAlignment = HorizontalAlignment.Right;
                tk.SetPosition(new Vector2(right - 200f * u, hy)); tk.SetSize(new Vector2(200f * u, 0f));
                float tkW = MeasureLabel(tk);
                var dot = Next(ref cur, 22, EDot);
                dot.Text = "·";
                float dotX = right - tkW - 16f * u;
                dot.SetPosition(new Vector2(dotX, hy));
                var dl = Next(ref cur, 22, EDealt);
                dl.Text = CountText(item.Dealt, 0);
                dl.HorizontalAlignment = HorizontalAlignment.Right;
                dl.SetPosition(new Vector2(dotX - 8f * u - 200f * u, hy)); dl.SetSize(new Vector2(200f * u, 0f));
            }

            // Split bar: the round's dealt share (blue) | taken share (red).
            float sy = y0 + RoundCards.SplitY(m);
            if (sy >= viewTop && sy + m.SplitH <= viewBottom)
            {
                float bw = w - 2f * m.PadX, bx = x + m.PadX, r = m.SplitH * 0.5f;
                Box(ref boxCur, bx, sy, bw, m.SplitH, splitTrack, default, 0f, r);
                var (dw, tw) = RoundCards.SplitBar(item.Dealt, item.Taken, bw);
                if (dw > 0f) Box(ref boxCur, bx, sy, dw, m.SplitH, EDealt, default, 0f, r, right: tw <= 0f);
                if (tw > 0f) Box(ref boxCur, bx + dw, sy, tw, m.SplitH, ETaken, default, 0f, r, left: dw <= 0f);
            }

            // Rows: icon | text | amount | bar, a thin line above each.
            for (int i = 0; i < item.Count; i++)
            {
                float ry = y0 + RoundCards.RowY(i, m);
                if (ry < viewTop || ry + m.RowH > viewBottom) continue;
                var e = log[item.First + i];
                var sep = NextBar(ref barCur);
                sep.Color = line;
                sep.SetPosition(new Vector2(x + m.PadX, ry - m.Gap * 0.5f));
                sep.SetSize(new Vector2(w - 2f * m.PadX, Math.Max(1f, u * 0.67f)));

                var tex = LogIcon(e);
                if (tex != null)
                {
                    var ic = NextIcon(ref iconCur);
                    ic.Texture = tex;
                    ic.SetPosition(new Vector2(cols.IconX, ry + (m.RowH - iconSz) * 0.5f));
                    ic.SetSize(new Vector2(iconSz, iconSz));
                }
                string text = e.AmountText == null ? e.Lead : oneEnemy && e.Kind == HitKind.Dealt ? e.ShortLead : e.Lead;
                var tl = Next(ref cur, 28, ECream, FontRole.Body, text, cols.TextW);
                tl.VerticalAlignment = VerticalAlignment.Center;
                tl.SetPosition(new Vector2(cols.TextX, ry)); tl.SetSize(new Vector2(cols.TextW, m.RowH));
                if (e.AmountText == null) continue;   // potions: no amount, no bar

                var (amountColor, barColor) = e.Kind switch
                {
                    HitKind.Taken or HitKind.Absorbed => (ATaken, ETaken),
                    HitKind.Buffered => (ASaved, ESavedBar),
                    HitKind.Healed => (AHealed, EHealedBar),
                    _ => (ADealt, coop ? palette[Math.Clamp(e.Slot, 0, palette.Length - 1)] : EDealt),
                };
                var al = Next(ref cur, 28, amountColor, FontRole.Numeric, e.AmountText, cols.AmountW);
                al.HorizontalAlignment = HorizontalAlignment.Right;
                al.VerticalAlignment = VerticalAlignment.Center;
                al.SetPosition(new Vector2(cols.AmountX, ry)); al.SetSize(new Vector2(cols.AmountW, m.RowH));

                float bh = 12f * u, by = ry + (m.RowH - bh) * 0.5f;
                Box(ref boxCur, cols.BarX, by, cols.BarW, bh, barTrack, default, 0f, bh * 0.5f);
                float fw = LogLayout.BarWidth(e.Amount, maxAmount, cols.BarW, bh);
                if (fw > 0f) Box(ref boxCur, cols.BarX, by, fw, bh, barColor, default, 0f, bh * 0.5f);
            }
        }
    }

    // A pooled rounded panel (StyleBoxFlat): `left`/`right` false square off that side's corners (a split bar's
    // inner ends). Drawn beneath bars, labels and icons, in call order.
    private void Box(ref int cursor, float x, float y, float w, float h, Color bg, Color border, float borderW, float radius,
                     bool left = true, bool right = true)
    {
        if (w <= 0f || h <= 0f) return;
        PooledBox b;
        if (cursor < _boxes.Count) b = _boxes[cursor];
        else
        {
            var style = new StyleBoxFlat { AntiAliasing = true };
            var panel = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
            Th.Stylebox(panel, "panel", style);
            _boxLayer.AddChild(panel);
            b = new PooledBox(panel, style);
            _boxes.Add(b);
        }
        cursor++;
        int bw = borderW > 0f ? Math.Max(1, (int)Math.Round(borderW)) : 0;
        int r = (int)Math.Round(Math.Min(radius, Math.Min(w, h) * 0.5f));
        var want = (bg, border, bw, r, left, right);
        if (b.Last != want)
        {
            var s = b.Style;
            s.BgColor = bg;
            s.BorderColor = border;
            s.BorderWidthLeft = bw; s.BorderWidthTop = bw; s.BorderWidthRight = bw; s.BorderWidthBottom = bw;
            s.CornerRadiusTopLeft = left ? r : 0; s.CornerRadiusBottomLeft = left ? r : 0;
            s.CornerRadiusTopRight = right ? r : 0; s.CornerRadiusBottomRight = right ? r : 0;
            b.Last = want;
        }
        b.Panel.Visible = true;
        b.Panel.SetPosition(new Vector2(x, y));
        b.Panel.SetSize(new Vector2(w, h));
    }

    private void RenderColumn(ref int cur, ref int barCur, ref int iconCur, float x, float top, float colW,
                              float rowH, int maxRows, bool full, string title,
                              List<SourceEntry> entries, long total, Color accent, bool taken)
    {
        var head = Next(ref cur, full ? 16 : 13, accent, FontRole.Numeric);
        head.Text = $"{title}   {total}";
        head.SetPosition(new Vector2(x, top));

        float y = top + (full ? 30f * _fontScale : 24f);
        if (entries.Count == 0)
        {
            var none = Next(ref cur, full ? 14 : 12, new Color(1f, 1f, 1f, 0.5f));
            none.Text = "—";
            none.SetPosition(new Vector2(x, y));
            return;
        }

        float iconSz = full ? 24f * _fontScale : IconSize;
        int textSize = full ? 14 : 12;
        long max = entries[0].Total > 0 ? entries[0].Total : 1; // sorted desc
        int shown = Math.Min(entries.Count, maxRows);
        for (int i = 0; i < shown; i++)
        {
            var e = entries[i];
            int pct = total > 0 ? (int)Math.Round(100.0 * e.Total / total) : 0;

            // Bar scaled to the column's largest source, drawn behind the text.
            float bw = colW * (float)e.Total / max;
            var bar = NextBar(ref barCur);
            // Full view: a dark, opaque tint of the player color so the cream source text stays legible
            // (white-on-light fails contrast). Summary view keeps the light translucent wash.
            bar.Color = _opaque
                ? new Color(accent.R * 0.4f, accent.G * 0.4f, accent.B * 0.4f, 1f)
                : new Color(accent.R, accent.G, accent.B, 0.30f);
            bar.SetPosition(new Vector2(x - 2f, y - 1f));
            bar.SetSize(new Vector2(Math.Max(bw, 2f), rowH - 4f));

            // Card/relic icon at the row head, if we have art for this source (native_visuals: else a
            // best-effort game icon from the source's name / an enemy-attack intent icon; else text only).
            float textX = x;
            var tex = e.Icon != null ? LoadTex(e.Icon) : null;
            tex ??= FallbackIcon(e.Name, e.Total, taken);
            if (tex != null)
            {
                var ic = NextIcon(ref iconCur);
                ic.Texture = tex;
                ic.SetPosition(new Vector2(x, y - 1f));
                ic.SetSize(new Vector2(iconSz, iconSz));
                textX = x + iconSz + 4f;
            }

            if (GameAssets.NumericFont != null)
            {
                // Native: name on the left, total + share right-aligned in the game's numeric Kreon.
                var name = Next(ref cur, textSize, UiTheme.Cream);
                name.Text = Truncate(e.Name, full ? 24 : 18);
                name.SetPosition(new Vector2(textX, y));
                var val = Next(ref cur, textSize, UiTheme.Cream, FontRole.Numeric);
                val.Text = $"{e.Total} ({pct}%)";
                val.HorizontalAlignment = HorizontalAlignment.Right;
                val.SetPosition(new Vector2(textX, y));
                val.SetSize(new Vector2(Math.Max(0f, x + colW - 6f - textX), 0f));
            }
            else
            {
                var row = Next(ref cur, textSize, UiTheme.Cream);
                row.Text = $"{Truncate(e.Name, full ? 24 : 18)}  {e.Total} ({pct}%)";
                row.SetPosition(new Vector2(textX, y));
            }
            y += rowH;
        }
        if (entries.Count > shown)
        {
            var more = Next(ref cur, full ? 13 : 11, new Color(1f, 1f, 1f, 0.5f));
            more.Text = $"+ {entries.Count - shown} more";
            more.SetPosition(new Vector2(x, y));
        }
    }

    private readonly Dictionary<int, string> _roundLabels = new();
    private string? _healIconPath;
    private bool _healIconResolved;

    // The summary's combat log: the full view's round cards (RenderCards), scaled to the summary's width by the same
    // rules (RoundCards.LogColumn: the text keeps its minimum, the bar track gives way first). Row text at about
    // 13 px at ui_scale 1 (SummaryU = 0.7 mockup px per px x 1.5 / 1.5).
    private const float SummaryU = 0.7f;

    private void RenderSummaryLog(ref int cur, ref int barCur, ref int iconCur, ref int boxCur, SourceSnapshot snap,
                                  Color[] palette, float top)
    {
        const float pad = 14f, sbSpace = 10f;
        var head = Next(ref cur, 13, UiTheme.Gold, FontRole.Bold);
        head.Text = "Combat Log";
        head.SetPosition(new Vector2(pad, top));
        float viewTop = top + LogHeadH;
        float viewH = Math.Max(1f, LogListBottom - viewTop);
        float logW = _w - 2f * pad - sbSpace;
        var (u, barW) = RoundCards.LogColumn(logW, SummaryU * Math.Clamp(UiScaleMul, 0.5f, 4f));
        float scale = _fontScale;
        _fontScale = u / 1.5f;                     // RenderCards sizes text by the view's scale
        try
        {
            _eNative = EnsureFrameStyles();
            RenderCards(ref cur, ref barCur, ref iconCur, ref boxCur, snap.Log, palette, snap.PlayerCount > 1,
                pad, logW, viewTop, viewH, u, barW);
        }
        finally { _fontScale = scale; }
        RenderScrollbar(false, viewTop, viewH, _w - pad + 2f, 6f, _visible + _maxOffset);
    }

    // The log's scrollbar at x = sbX over [listTop, listTop + regionH], from _logOffset/_maxOffset/_visible;
    // `units` is the scrollable content in scroll steps (for the fallback grabber's length).
    private void RenderScrollbar(bool full, float listTop, float regionH, float sbX, float sbW, int units)
    {
        _trackY = listTop; _trackH = regionH;
        _sbX = sbX;
        _sbHitL = _sbX - 6f; _sbHitR = _sbX + 12f;
        if (RenderNativeScrollbar(full, sbW))
        {
            _track.Visible = false; _grabber.Visible = false;
        }
        else if (_maxOffset > 0)
        {
            _grabberH = Math.Max(24f, _trackH * _visible / Math.Max(1, units));
            _grabberY = _trackY + (_trackH - _grabberH) * ((float)_logOffset / _maxOffset);
            _track.Visible = true; _track.SetPosition(new Vector2(_sbX, _trackY)); _track.SetSize(new Vector2(sbW, _trackH));
            _grabber.Visible = true; _grabber.SetPosition(new Vector2(_sbX, _grabberY)); _grabber.SetSize(new Vector2(sbW, _grabberH));
        }
        else { _track.Visible = false; _grabber.Visible = false; _grabberH = 0f; }
    }

    // The game's scrollbar (scenes/ui/scrollbar.tscn): a tinted track body with square end caps (the
    // same edge sprite, flipped at the bottom) and a fixed-size knob that rides it, 1.5x the track width
    // (the game's 72px knob on a 48px track). Large art in the takeover, the small set in the summary.
    // False (and native pieces hidden) when native_visuals is off, the art is missing, or nothing scrolls;
    // the caller then draws the original ColorRect bar.
    private bool RenderNativeScrollbar(bool full, float sbW)
    {
        bool ok = false;
        try
        {
            if (_maxOffset > 0 && GameAssets.Enabled)
            {
                var track = full ? (_nTrackStyle ??= GameAssets.ScrollTrackStyle(false))
                                 : (_nTrackStyleSmall ??= GameAssets.ScrollTrackStyle(true));
                var knob = GameAssets.ScrollHandleTexture(!full);
                if (track != null && knob != null)
                {
                    float tw = full ? 18f * _fontScale : 10f;
                    float tx = _sbX + sbW * 0.5f - tw * 0.5f;           // centered on the old bar's line
                    float ks = tw * 1.5f;
                    var cap = GameAssets.Texture(GameAssets.Paths.UiAtlasSprites + (full ? "scrollbar_track_edge2.tres" : "small_scrollbar_track_edge.tres"));
                    // Caps keep the edge sprite's aspect (scrollbar.tscn: expand_mode fit-height-proportional;
                    // the large edge is 60x36, the small one square).
                    float capH = cap != null ? Math.Min(tw * cap.GetHeight() / Math.Max(1f, cap.GetWidth()), _trackH * 0.25f) : 0f;

                    if (_nTrackApplied != track) { Th.Stylebox(_nTrack, "panel", track); _nTrackApplied = track; }
                    _nTrack.Visible = true;
                    _nTrack.SetPosition(new Vector2(tx, _trackY + capH));
                    _nTrack.SetSize(new Vector2(tw, Math.Max(1f, _trackH - capH * 2f)));
                    _nCapTop.Visible = _nCapBot.Visible = cap != null;
                    if (cap != null)
                    {
                        _nCapTop.Texture = cap; _nCapBot.Texture = cap;
                        _nCapTop.SetPosition(new Vector2(tx, _trackY)); _nCapTop.SetSize(new Vector2(tw, capH));
                        _nCapBot.SetPosition(new Vector2(tx, _trackY + _trackH - capH)); _nCapBot.SetSize(new Vector2(tw, capH));
                    }

                    // Fixed-size knob: same travel math as the old grabber (drag maps over _trackH - _grabberH).
                    _grabberH = Math.Min(ks, _trackH);
                    _grabberY = _trackY + (_trackH - _grabberH) * ((float)_logOffset / _maxOffset);
                    _nKnob.Texture = knob;
                    _nKnob.Visible = true;
                    _nKnob.SetPosition(new Vector2(tx + tw * 0.5f - ks * 0.5f, _grabberY));
                    _nKnob.SetSize(new Vector2(ks, _grabberH));

                    _sbHitL = Math.Min(_sbHitL, tx + tw * 0.5f - ks * 0.5f);
                    _sbHitR = Math.Max(_sbHitR, tx + tw * 0.5f + ks * 0.5f);
                    ok = true;
                }
            }
        }
        catch { ok = false; }
        if (!ok) { _nTrack.Visible = false; _nCapTop.Visible = false; _nCapBot.Visible = false; _nKnob.Visible = false; }
        return ok;
    }

    // Takeover backdrop: the game's own screen-space blur (shaders/dark_blur.gdshader, uniforms lod and
    // mix_percentage), eased in to NMainMenu's backstop values (lod 3, mix 0.7) like its 0.25s tween. The
    // shader overwrites COLOR, so the ColorRect's dim color only shows when it's absent (the fallback).
    private void UpdateBlur()
    {
        try
        {
            if (_blurMat == null && GameAssets.Shader(GameAssets.Paths.DarkBlurShader) is Shader sh)
                _blurMat = new ShaderMaterial { Shader = sh };
            if (_blurMat == null) { _dim.Material = null; return; }
            _blurT += (1f - _blurT) * 0.25f;
            _blurMat.SetShaderParameter(BlurLod, 3f * _blurT);
            _blurMat.SetShaderParameter(BlurMix, 0.7f * _blurT);
            if (_dim.Material != _blurMat) _dim.Material = _blurMat;
        }
        catch { try { _dim.Material = null; } catch { } }
    }

    // Summary frame: the game's hover-tip panel art (plus its drop shadow), applied once when available.
    private bool EnsureNativePanel()
    {
        if (_nativePanel) return true;
        try
        {
            var panel = GameAssets.NativePanel();
            if (panel == null) return false;
            var shadow = GameAssets.NativePanelShadow();
            Th.Stylebox(_bg, "panel", panel);
            if (shadow != null) Th.Stylebox(_bgShadow, "panel", shadow);
            else Th.Stylebox(_bgShadow, "panel", new StyleBoxEmpty());
            _nativePanel = true;
        }
        catch { }
        return _nativePanel;
    }

    // native_visuals fallback for a source row with no stamped icon. Taken-side labels never carry an icon
    // and pets/statuses may not, so first try the game's own sprite for the name (English titles map onto
    // power/relic/potion atlas entries, e.g. "Poison" -> poison_power.tres); else, for damage taken from a
    // named attacker, the enemy attack-intent icon sized by the game's thresholds. Null = text-only row.
    private Texture2D? FallbackIcon(string name, long total, bool taken)
    {
        if (!GameAssets.Enabled || string.IsNullOrEmpty(name)) return null;
        try
        {
            var tex = _nameIconCache.Get(name, NameIcon);
            if (tex != null) return tex;
            if (taken && name is not ("Status" or "Other" or "Unknown"))
                return GameAssets.IntentAttackIcon((int)Math.Min(total, int.MaxValue));
        }
        catch { }
        return null;
    }

    private string RoundLabel(int round)
    {
        if (!_roundLabels.TryGetValue(round, out var text)) { text = $"Round {round}"; _roundLabels[round] = text; }
        return text;
    }

    private void DividerLine(ref int barCur, float x, float w, float y, float thick, Color color)
    {
        if (w <= 0f) return;
        var line = NextBar(ref barCur);
        line.Color = color;
        line.SetPosition(new Vector2(x, y));
        line.SetSize(new Vector2(w, thick));
    }

    // A log line's icon: Buffer lines the Buffer power, heals a heal icon, else the hit's own art (card, power,
    // potion), else the source's game sprite by name or an attack intent for damage taken. Every lookup is cached
    // (LoadTex, _nameIconCache, GameAssets), so nothing is loaded per frame.
    private Texture2D? LogIcon(in LogEntry e)
    {
        if (e.Kind is not HitKind kind) return null;
        try
        {
            switch (kind)
            {
                case HitKind.Buffered:
                    return GameAssets.Enabled ? LoadTex("res://images/atlases/power_atlas.sprites/buffer_power.tres") : null;
                case HitKind.Healed:
                    return HealIcon();
            }
            if (e.Icon != null && LoadTex(e.Icon) is { } own) return own;
            return FallbackIcon(e.Name ?? "", e.Amount, kind is HitKind.Taken or HitKind.Absorbed);
        }
        catch { return null; }
    }

    // The game's heal intent (frame 0), else the top bar's heart; null with native_visuals off. Resolved once.
    private Texture2D? HealIcon()
    {
        if (!GameAssets.Enabled) return null;
        if (_healIconResolved) return _healIconPath != null ? LoadTex(_healIconPath) : null;
        _healIconResolved = true;
        foreach (var p in new[]
        {
            "res://images/atlases/intent_atlas.sprites/heal/intent_heal_00.tres",
            "res://images/packed/intents/heal/intent_heal_00.png",
            ETopBarHeart,
        })
            if (LoadTex(p) is { } tex) { _healIconPath = p; return tex; }
        return null;
    }

    // A label's text width at its applied font and size (0 when unknown, which centers it as if empty).
    private static float MeasureLabel(Label lbl)
    {
        try
        {
            var font = lbl.GetThemeFont("font");
            int size = lbl.GetThemeFontSize("font_size");
            return font != null ? font.GetStringSize(lbl.Text, HorizontalAlignment.Left, -1, size).X : 0f;
        }
        catch { return 0f; }
    }

    private static Texture2D? NameIcon(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char ch in name.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if ((ch == ' ' || ch == '-' || ch == '_') && sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        string slug = sb.ToString().Trim('_');
        if (slug.Length == 0) return null;
        // Probe with Exists first so names that aren't game entries (monsters, cards) don't each log a
        // GameAssets "unavailable" line.
        foreach (var p in new[]
        {
            $"res://images/atlases/power_atlas.sprites/{slug}_power.tres",
            $"res://images/atlases/power_atlas.sprites/{slug}.tres",
            $"res://images/atlases/relic_atlas.sprites/{slug}.tres",
            $"res://images/atlases/potion_atlas.sprites/{slug}.tres",
            $"res://images/orbs/{slug}_orb.png",   // orb damage labeled by the orb's title ("Lightning")
            $"res://images/orbs/{slug}.png",
        })
        {
            try { if (ResourceLoader.Exists(p) && GameAssets.Texture(p) is Texture2D t) return t; }
            catch { }
        }
        return null;
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

    // "POTIONS" then each potion used (icon, name, xN when repeated) flowing left to right; whatever doesn't
    // fit the width collapses into "+ N more".
    private void RenderPotions(ref int cur, ref int iconCur, float x, float y, float width, List<SourceEntry> potions, bool full)
    {
        int textSize = full ? 14 : 12;
        float iconSz = full ? 22f * _fontScale : 18f;
        var green = new Color(0.55f, 0.85f, 0.55f);
        var head = Next(ref cur, textSize, green, FontRole.Bold);
        head.Text = "POTIONS";
        head.SetPosition(new Vector2(x, y));
        float cx = x + (full ? 90f * _fontScale : 66f);
        float right = x + width;
        float charW = textSize * _fontScale * 0.55f;   // rough Kreon advance, for fitting only
        for (int i = 0; i < potions.Count; i++)
        {
            var e = potions[i];
            string label = e.Total > 1 ? $"{e.Name} x{e.Total}" : e.Name;
            float need = iconSz + 4f + label.Length * charW + 14f;
            if (cx + need > right && i > 0)
            {
                var more = Next(ref cur, textSize, new Color(1f, 1f, 1f, 0.5f));
                more.Text = $"+ {potions.Count - i} more";
                more.SetPosition(new Vector2(cx, y));
                break;
            }
            var tex = e.Icon != null ? LoadTex(e.Icon) : null;
            if (tex != null)
            {
                var ic = NextIcon(ref iconCur);
                ic.Texture = tex;
                ic.SetPosition(new Vector2(cx, y - 2f));
                ic.SetSize(new Vector2(iconSz, iconSz));
                cx += iconSz + 4f;
            }
            var lbl = Next(ref cur, textSize, UiTheme.Cream);
            lbl.Text = label;
            lbl.SetPosition(new Vector2(cx, y));
            cx += label.Length * charW + 14f;
        }
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

    private Texture2D? LoadTex(string path) => _texCache.Get(path, ProbeTex);

    internal static Texture2D? ProbeTex(string path)
    {
        try { return ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path, null, ResourceLoader.CacheMode.Reuse) : null; }
        catch { return null; }
    }

    // Largest size <= px (down to a readable floor) at which `text` fits `maxW`, allowing for the outline.
    // Cached by (text, size, width): fitted labels repeat every frame and measuring goes through Godot interop.
    // A column too narrow even at the floor gets the floor size (the label then trims with an ellipsis).
    private readonly Dictionary<(string, int, int), int> _fitCache = new();
    private int FitFontSize(Font? font, string text, int px, float maxW)
    {
        if (font == null || text.Length == 0) return px;
        var key = (text, px, (int)maxW);
        if (_fitCache.TryGetValue(key, out int cached)) return cached;
        if (_fitCache.Count > 4000) _fitCache.Clear();
        int size;
        try { size = LogLayout.FitSize(sz => font.GetStringSize(text, HorizontalAlignment.Left, -1, sz).X + 8f, px, maxW); }
        catch { size = px; }
        _fitCache[key] = size;
        return size;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";


    // Which game Kreon a label gets under native_visuals (Body = UiTheme's font, unchanged when off).
    private enum FontRole { Body, Bold, Numeric }

    // With fitText, the label shows that text at the largest size up to fontSize whose width fits fitWidth
    // (sized before applying, so the font size isn't rewritten every frame).
    // outline: the outline size in px (null = UiTheme's default); shadow: the hover tips' soft drop shadow
    // (the game's title look), removed again when a pooled label is reused without it.
    private Label Next(ref int cursor, int fontSize, Color color, FontRole role = FontRole.Body,
                       string? fitText = null, float fitWidth = 0f, int? outline = null, bool shadow = false)
    {
        if (_opaque) color = new Color(color.R, color.G, color.B, 1f);
        int fs = Math.Max(1, (int)Math.Round(fontSize * _fontScale));
        Label lbl;
        if (cursor < _labels.Count) lbl = _labels[cursor];
        else { lbl = UiTheme.MakeLabel(fs, color); _container.AddChild(lbl); _labels.Add(lbl); }
        Font? roleFont = role == FontRole.Body ? null : (role == FontRole.Bold ? GameAssets.KreonBold : GameAssets.NumericFont);
        if (fitText != null)
        {
            Font? measure = roleFont ?? UiTheme.DefaultFont;   // what UiTheme.Apply below will use
            try { measure ??= lbl.GetThemeFont("font"); } catch { }
            fs = FitFontSize(measure, fitText, fs, fitWidth);
            if (lbl.Text != fitText) lbl.Text = fitText;
        }
        // Pooled labels may have been a right-aligned, centered, sized or trimming label last frame. A fitted
        // label is as wide as its column and trims with an ellipsis if even the floor size doesn't fit.
        if (GameAssets.Enabled && roleFont == null && UiTheme.DefaultFont == null) Th.RemoveFont(lbl, "font");
        lbl.HorizontalAlignment = HorizontalAlignment.Left;
        lbl.VerticalAlignment = VerticalAlignment.Top;
        var overrun = fitText != null ? TextServer.OverrunBehavior.TrimEllipsis : TextServer.OverrunBehavior.NoTrimming;
        if (lbl.TextOverrunBehavior != overrun) lbl.TextOverrunBehavior = overrun;
        lbl.SetSize(fitText != null ? new Vector2(Math.Max(0f, fitWidth), 0f) : Vector2.Zero);
        UiTheme.Apply(lbl, fs, color, roleFont, outlineSize: outline);
        if (shadow)
        {
            Th.Color(lbl, "font_shadow_color", ETextShadow);
            Th.Const(lbl, "shadow_offset_x", Math.Max(1, (int)Math.Round(fs * 3f / 22f)));
            Th.Const(lbl, "shadow_offset_y", Math.Max(1, (int)Math.Round(fs * 2f / 22f)));
        }
        else Th.RemoveColor(lbl, "font_shadow_color");
        lbl.Visible = true;
        cursor++;
        return lbl;
    }
}
