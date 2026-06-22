using System;
using System.Collections.Generic;
using Godot;

namespace STS2_DamageCharts;

// The breakdown view. Two modes:
//  - Interactive (hotkey-toggled): a full-screen takeover — dimmed backdrop, two wide columns
//    (Dealt / Taken by source) over a tall scrollable combat log. Sized to the viewport.
//  - Summary (auto end-of-combat): a compact, click-through panel on the left over the rewards
//    moment.
// Native ScrollContainers can't be used (no engine GUI callbacks reach our CanvasLayer), so the
// log scrolls via a poll-driven draggable scrollbar plus mouse-wheel routed in from the mod's
// input hook (see ScrollBy).
internal sealed class DamageDetailView
{
    private const float SummaryWidth = 480f;
    private const float SummaryHeight = 540f;
    private const int SummaryMaxRows = 10;

    private readonly Node _root;
    private readonly CanvasLayer _layer;
    private readonly Control _container;
    // Reparent target: in full-screen mode we mount _container into the game's NGlobalUi just beneath
    // the NTopBar node so the top bar floats over us (like the native deck/map). Falls back to _layer
    // (our own CanvasLayer, above everything) when the game nodes aren't available, and for summary mode.
    private Node? _globalUi;
    private Node? _topBar;
    private bool _panelVisible;
    private readonly Control _inputBlock;  // full-screen Stop-filter blocker (full takeover only)
    private readonly ColorRect _dim;      // full-screen dim backdrop (interactive mode only)
    private readonly Panel _bg;           // themed panel (summary mode)
    private readonly Control _barLayer;   // behind text
    private readonly List<Label> _labels = new();
    private readonly List<ColorRect> _bars = new();
    private readonly List<TextureRect> _icons = new();
    private readonly Dictionary<string, Texture2D?> _texCache = new();
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
    private int _lastLogCount = -1;

    // When true, render as an end-of-combat summary (compact, click-through over rewards).
    public bool SummaryMode;
    // When true, the full-screen takeover is the run recap: adds a Dealt/Taken/Healed/Block totals band
    // and relabels the chart axis per-fight instead of per-round. Only meaningful when !SummaryMode.
    public bool RecapMode;
    // In-combat full view only: when set with RunFights, draw a run-history band (per prior combat
    // dealt/taken/heal/block plus a run total) above the current-combat columns.
    public bool ShowRunHistory;
    public List<RunAggregate.EncounterStat>? RunFights;
    // Display string for the toggle key, shown in the header (set by the mod from config).
    public string HotkeyHint = "C";

    public DamageDetailView(Node root)
    {
        _root = root;
        _layer = new CanvasLayer { Layer = 129, Name = "Sts2DamageChartsDetail", Visible = false };
        root.AddChild(_layer);

        _container = new Control { Name = "Detail", MouseFilter = Control.MouseFilterEnum.Ignore };
        _container.SetSize(new Vector2(_w, _h));
        _layer.AddChild(_container);

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

        _bg = UiTheme.MakePanel(0.86f, 1f);
        _bg.SetPosition(Vector2.Zero);
        _bg.SetSize(new Vector2(_w, _h));
        _container.AddChild(_bg);

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

        // Close/back button (full-screen only); clicks detected by polling in UpdateMouse. Added last → on
        // top. Visual only (MouseFilter.Ignore); we animate it ourselves in Render.
        _btn = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        // High-contrast styling so the button reads against the dark takeover backdrop (a dark themed
        // panel blended in): a lighter slate fill with a bright gold border.
        var btnStyle = new StyleBoxFlat { BgColor = new Color(0.20f, 0.23f, 0.32f, 0.96f), BorderColor = UiTheme.Gold };
        btnStyle.SetCornerRadiusAll(8);
        btnStyle.SetBorderWidthAll(3);
        _btn.AddThemeStyleboxOverride("panel", btnStyle);
        _container.AddChild(_btn);
        _btnGlyph = UiTheme.MakeLabel(20, UiTheme.Gold);
        _btnGlyph.Text = "←";
        _btnGlyph.HorizontalAlignment = HorizontalAlignment.Center;
        _btnGlyph.VerticalAlignment = VerticalAlignment.Center;
        _btnGlyph.MouseFilter = Control.MouseFilterEnum.Ignore;
        _btnGlyph.Visible = false;
        _container.AddChild(_btnGlyph);
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
        }
        catch { }
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
            // Summary mode: only the ✕ is interactive (no scrollbar). Stop after close detection.
            if (SummaryMode) { _leftLast = leftDown; return; }
            bool overTrackX = m.X >= _sbX - 6f && m.X <= _sbX + 12f;
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

    // _container can be freed by the game if it was reparented under NGlobalUi and the run ends, so
    // validity requires both our layer and the container to still be alive.
    public bool IsValid() => GodotObject.IsInstanceValid(_layer) && GodotObject.IsInstanceValid(_container);
    public bool Visible => IsValid() && _panelVisible;
    public void SetVisible(bool v)
    {
        if (v && !_panelVisible) { _btnShow = 0f; _btnHover = 0f; _btnPress = 0f; _btnHovered = false; _btnPressed = false; }
        _panelVisible = v;
        if (!IsValid()) return;
        _layer.Visible = v;
        try { _container.Visible = v; } catch { }
    }
    public void Dispose()
    {
        try { if (GodotObject.IsInstanceValid(_container) && _container.GetParent() != _layer) _container.GetParent()?.RemoveChild(_container); } catch { }
        try { if (GodotObject.IsInstanceValid(_container)) _container.QueueFree(); } catch { }
        try { if (GodotObject.IsInstanceValid(_layer)) _layer.QueueFree(); } catch { }
    }

    // Mount _container beneath the game's TopBar (full mode) or back under our CanvasLayer (summary /
    // fallback). Guarded so a missing/stale game node can never disrupt rendering or combat.
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

            if (_container.GetParent() != _layer)
            {
                _container.GetParent()?.RemoveChild(_container);
                _layer.AddChild(_container);
            }
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

    // Top padding (viewport px) that pushes full-screen content below the game's top bar (~8% of
    // viewport height, letterbox-excluded) while the dim backdrop stays full-bleed. 9% leaves a hair.
    private static float TopBarInset(Vector2 vp) => Math.Max(48f, vp.Y * 0.09f);

    public void Render(SourceSnapshot snap, ChartSnapshot chart, Color[] palette)
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
            if (full) { _dim.SetPosition(Vector2.Zero); _dim.SetSize(new Vector2(_w, _h)); }
            // Input-catcher: full-screen under the takeover (suppresses stray game tooltips), or just over
            // the summary panel (so the mouse wheel scrolls the summary log instead of the rewards screen
            // underneath eating it). Outside the summary panel stays click-through.
            _inputBlock.Visible = full || SummaryMode;
            if (full) { _inputBlock.SetPosition(Vector2.Zero); _inputBlock.SetSize(vp); }
            else if (SummaryMode) { _inputBlock.SetPosition(_container.Position); _inputBlock.SetSize(new Vector2(_w, _h)); }
            _bg.Visible = !full;
            if (!full) { _bg.SetPosition(Vector2.Zero); _bg.SetSize(new Vector2(_w, _h)); }
            _barLayer.SetSize(new Vector2(_w, _h));

            float pad = full ? Math.Max(28f, _w * 0.05f) : 14f;
            float gap = full ? pad : 12f;
            float headerY = full ? top + pad * 0.6f : 8f;
            float colTop = headerY + (full ? 48f * _fontScale : 32f);
            // Recap reserves a totals band (Dealt/Taken/Healed/Block) between the header and the columns.
            // The in-combat view instead reserves a run-history band (per prior combat). They never
            // co-occur (recap is out of combat), so they share the same slot below the header.
            bool recap = full && RecapMode;
            bool history = full && !recap && ShowRunHistory && RunFights != null && RunFights.Count > 0;
            float bandTop = colTop;
            int histShown = history ? Math.Min(RunFights!.Count, 6) : 0;
            if (recap) colTop += 56f * _fontScale;
            else if (history) colTop += (34f + (histShown + 1) * 16f) * _fontScale;
            float colW = (_w - pad * 2f - gap) * 0.5f;
            float leftX = pad;
            float rightX = pad + colW + gap;
            // Full-screen stacks three bands within the padded content area: by-source columns, the
            // Damage/Round chart, then the log.
            float chartTop = full ? top + (_h - top) * 0.32f : 0f;
            float logTop = full ? top + (_h - top) * 0.58f : 296f;
            float rowH = full ? 26f * _fontScale : 22f;
            // Column rows: as many as fit between the column head and the next band.
            int maxRows = full
                ? Math.Max(3, (int)((chartTop - pad - (colTop + 24f)) / rowH))
                : SummaryMaxRows;

            // Close/back button, full-screen only: a self-drawn button (bottom-left, like deck/map) that we
            // animate ourselves — fades + scales in on open, grows/brightens on hover, depresses on press.
            // The click is detected by polling _closeRect in UpdateMouse.
            if (full)
            {
                float bw = 120f * _fontScale, bh = 84f * _fontScale;
                float bx = pad, by = _h - bh - pad * 0.5f;
                _closeRect = new Rect2(bx, by, bw, bh);
                _bottomReserve = bh + pad;                    // distance from screen bottom to keep the log clear of the button

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

            int cur = 0, barCur = 0, iconCur = 0;
            string who = (snap.PlayerCount > 1 && slot < snap.Labels.Length) ? $" — {snap.Labels[slot]}" : "";

            var header = Next(ref cur, full ? 22 : 16, UiTheme.Gold);
            header.Text = recap
                ? $"Run Recap{who}     ({HotkeyHint} to close)"
                : SummaryMode
                    ? $"Combat Summary{who}     ({HotkeyHint} or ✕ to dismiss)"
                    : $"Combat Stats{who}     ({HotkeyHint} to close)";
            if (full) { header.HorizontalAlignment = HorizontalAlignment.Center; header.SetSize(new Vector2(_w, 0f)); header.SetPosition(new Vector2(0f, headerY)); }
            else { header.HorizontalAlignment = HorizontalAlignment.Left; header.SetSize(Vector2.Zero); header.SetPosition(new Vector2(10f, headerY)); }

            if (recap)
                RenderTotalsBand(ref cur, pad, bandTop, _w - pad * 2f, ps, color);
            else if (history)
                RenderHistoryBand(ref cur, pad, bandTop, _w - pad * 2f, RunFights!, histShown);

            RenderColumn(ref cur, ref barCur, ref iconCur, leftX, colTop, colW, rowH, maxRows, full, "DEALT", ps.Dealt, ps.DealtTotal, color);
            RenderColumn(ref cur, ref barCur, ref iconCur, rightX, colTop, colW, rowH, maxRows, full, "TAKEN", ps.Taken, ps.TakenTotal, UiTheme.Red);

            if (full)
                RenderRoundChart(ref cur, ref barCur, ref iconCur, chart, palette, pad, chartTop, _w - pad * 2f, logTop - chartTop - 10f, recap);

            RenderLog(ref cur, snap.Log, full, pad, logTop);

            for (int i = cur; i < _labels.Count; i++) _labels[i].Visible = false;
            for (int i = barCur; i < _bars.Count; i++) _bars[i].Visible = false;
            for (int i = iconCur; i < _icons.Count; i++) _icons[i].Visible = false;
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] detail render error: {ex.Message}"); }
    }

    // Run-recap totals strip: four big stats (Dealt / Taken / Healed / Block) spread across the width.
    private void RenderTotalsBand(ref int cur, float x, float top, float width, PlayerSources ps, Color dealtColor)
    {
        var heal = new Color(0.45f, 0.85f, 0.45f);
        var block = new Color(0.50f, 0.72f, 0.96f);
        var stats = new (string Caption, long Value, Color Color)[]
        {
            ("DEALT", ps.DealtTotal, dealtColor),
            ("TAKEN", ps.TakenTotal, UiTheme.Red),
            ("HEALED", ps.HealTotal, heal),
            ("BLOCK", ps.BlockTotal, block),
        };
        float cellW = width / stats.Length;
        for (int i = 0; i < stats.Length; i++)
        {
            float cx = x + i * cellW;
            var cap = Next(ref cur, 12, new Color(stats[i].Color.R, stats[i].Color.G, stats[i].Color.B, 0.75f));
            cap.Text = stats[i].Caption;
            cap.SetPosition(new Vector2(cx, top));
            var val = Next(ref cur, 26, stats[i].Color);
            val.Text = stats[i].Value.ToString();
            val.SetPosition(new Vector2(cx, top + 16f * _fontScale));
        }
    }

    // In-combat run-history band: a compact table of each prior combat's Dealt/Taken/Heal/Block plus a
    // run total. Shows the most recent `shown` fights; the total sums all of them.
    private void RenderHistoryBand(ref int cur, float x, float top, float width,
                                   List<RunAggregate.EncounterStat> fights, int shown)
    {
        var dim = new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.6f);
        var heal = new Color(0.45f, 0.85f, 0.45f);
        var block = new Color(0.50f, 0.72f, 0.96f);
        Color[] cols = { dim, UiTheme.Cream, UiTheme.Red, heal, block };

        int n = fights.Count;
        var head = Next(ref cur, 13, UiTheme.Gold);
        head.Text = $"RUN SO FAR — {n} prior fight{(n == 1 ? "" : "s")}";
        head.SetPosition(new Vector2(x, top));

        float fightW = width * 0.16f;
        float statW = (width - fightW) / 4f;
        float[] cx = { x, x + fightW, x + fightW + statW, x + fightW + statW * 2f, x + fightW + statW * 3f };
        string[] heads = { "FIGHT", "DEALT", "TAKEN", "HEAL", "BLOCK" };

        float chY = top + 16f * _fontScale;
        for (int i = 0; i < heads.Length; i++)
        {
            var h = Next(ref cur, 11, new Color(cols[i].R, cols[i].G, cols[i].B, 0.7f));
            h.Text = heads[i];
            h.SetPosition(new Vector2(cx[i], chY));
        }

        long td = 0, tt = 0, th = 0, tb = 0;
        foreach (var f in fights) { td += f.Dealt; tt += f.Taken; th += f.Heal; tb += f.Block; }

        float rowH = 16f * _fontScale;
        float y = top + 34f * _fontScale;
        int start = Math.Max(0, n - shown);
        for (int i = start; i < n; i++)
        {
            var f = fights[i];
            string[] vals = { f.Fight.ToString(), f.Dealt.ToString(), f.Taken.ToString(), f.Heal.ToString(), f.Block.ToString() };
            for (int c = 0; c < vals.Length; c++)
            {
                var lbl = Next(ref cur, 12, cols[c]);
                lbl.Text = vals[c];
                lbl.SetPosition(new Vector2(cx[c], y));
            }
            y += rowH;
        }
        string[] totals = { "TOTAL", td.ToString(), tt.ToString(), th.ToString(), tb.ToString() };
        for (int c = 0; c < totals.Length; c++)
        {
            var lbl = Next(ref cur, 12, c == 0 ? UiTheme.Gold : cols[c]);
            lbl.Text = totals[c];
            lbl.SetPosition(new Vector2(cx[c], y));
        }
    }

    private void RenderColumn(ref int cur, ref int barCur, ref int iconCur, float x, float top, float colW,
                              float rowH, int maxRows, bool full, string title,
                              List<SourceEntry> entries, long total, Color accent)
    {
        var head = Next(ref cur, full ? 16 : 13, accent);
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

            // Card/relic icon at the row head, if we have art for this source.
            float textX = x;
            if (e.Icon != null)
            {
                var tex = LoadTex(e.Icon);
                if (tex != null)
                {
                    var ic = NextIcon(ref iconCur);
                    ic.Texture = tex;
                    ic.SetPosition(new Vector2(x, y - 1f));
                    ic.SetSize(new Vector2(iconSz, iconSz));
                    textX = x + iconSz + 4f;
                }
            }

            var row = Next(ref cur, textSize, UiTheme.Cream);
            row.Text = $"{Truncate(e.Name, full ? 24 : 18)}  {e.Total} ({pct}%)";
            row.SetPosition(new Vector2(textX, y));
            y += rowH;
        }
        if (entries.Count > shown)
        {
            var more = Next(ref cur, full ? 13 : 11, new Color(1f, 1f, 1f, 0.5f));
            more.Text = $"+ {entries.Count - shown} more";
            more.SetPosition(new Vector2(x, y));
        }
    }

    // Per-round grouped bars (dealt stacked by source in player color, taken faded red) — the same
    // "Damage / Round" view as the always-on chart, sized to fill the breakdown's middle band.
    private void RenderRoundChart(ref int cur, ref int barCur, ref int iconCur, ChartSnapshot snap, Color[] palette,
                                  float x, float top, float w, float h, bool recap = false)
    {
        const int Cap = 12;             // show the most recent rounds (or fights); older scroll off
        var head = Next(ref cur, 16, UiTheme.Gold);
        head.Text = recap ? "Damage / Fight" : "Damage / Round";
        head.SetPosition(new Vector2(x, top));

        // Per-player legend (multiplayer only), to the right of the heading.
        if (snap.PlayerCount > 1)
        {
            float lx = x + 180f;
            for (int s = 0; s < snap.PlayerCount && s < palette.Length; s++)
            {
                var lbl = Next(ref cur, 12, palette[s]);
                lbl.Text = "■ " + (s < snap.Labels.Length ? snap.Labels[s] : $"P{s + 1}");
                lbl.SetPosition(new Vector2(lx, top));
                lx += 150f;
            }
        }

        float plotTop = top + 30f * _fontScale;
        float plotLeft = x + 38f, plotRight = x + w - 8f, plotBottom = top + h - 22f * _fontScale;
        float plotW = plotRight - plotLeft, plotH = plotBottom - plotTop;
        if (plotH < 12f || plotW < 12f) return;

        int total = snap.Rows.Count;
        if (total == 0)
        {
            var empty = Next(ref cur, 13, new Color(1f, 1f, 1f, 0.6f));
            empty.Text = "No damage yet";
            empty.SetPosition(new Vector2(plotLeft, plotTop + 6f));
            return;
        }

        int start = Math.Max(0, total - Cap);
        int groups = total - start;

        int maxVal = 1;
        for (int i = start; i < total; i++)
            for (int s = 0; s < snap.PlayerCount; s++)
            {
                if (snap.Rows[i].Dealt[s] > maxVal) maxVal = snap.Rows[i].Dealt[s];
                if (snap.Rows[i].Taken[s] > maxVal) maxVal = snap.Rows[i].Taken[s];
            }

        // Baseline + peak gridlines (thin ColorRects from the shared bar pool) with value labels.
        var gl0 = NextBar(ref barCur); gl0.Color = new Color(1f, 1f, 1f, 0.22f);
        gl0.SetPosition(new Vector2(plotLeft, plotBottom)); gl0.SetSize(new Vector2(plotW, 1f));
        var glT = NextBar(ref barCur); glT.Color = new Color(1f, 1f, 1f, 0.10f);
        glT.SetPosition(new Vector2(plotLeft, plotTop)); glT.SetSize(new Vector2(plotW, 1f));
        var axisColor = new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.55f);
        var peak = Next(ref cur, 14, axisColor); peak.Text = maxVal.ToString(); peak.SetPosition(new Vector2(x, plotTop - 6f));
        var zero = Next(ref cur, 14, axisColor); zero.Text = "0"; zero.SetPosition(new Vector2(x, plotBottom - 6f));

        // Each round group is only as wide as it needs to be (~ability-icon width per cell), left-
        // aligned, rather than stretched across the whole band.
        float cellW = Math.Min(64f, plotW / Math.Max(1, groups * snap.PlayerCount));
        float groupW = cellW * snap.PlayerCount;
        float usedW = Math.Min(plotW, groups * groupW);
        gl0.SetSize(new Vector2(usedW, 1f));
        glT.SetSize(new Vector2(usedW, 1f));
        for (int gi = 0; gi < groups; gi++)
        {
            var row = snap.Rows[start + gi];
            float groupX = plotLeft + gi * groupW;
            for (int s = 0; s < snap.PlayerCount; s++)
            {
                var c = palette[Math.Min(s, palette.Length - 1)];
                float cellX = groupX + s * cellW;
                float dealtW = cellW * 0.62f, takenW = cellW * 0.26f;
                float dealtX = cellX + cellW * 0.04f;
                float takenX = dealtX + dealtW + cellW * 0.04f;

                var agg = DamageChartView.AggregateBySource(row.DealtSegs[s]);
                float yTop = plotBottom;
                foreach (var a in agg)
                {
                    float bh = plotH * a.Sum / maxVal;
                    if (bh < 0.5f) continue;
                    var bar = NextBar(ref barCur);
                    bar.Color = Opaque(new Color(c.R, c.G, c.B, 0.95f));
                    bar.SetPosition(new Vector2(dealtX, yTop - bh));
                    bar.SetSize(new Vector2(dealtW, Math.Max(bh - 1f, 1f)));

                    if (a.Icon != null && bh >= 16f && dealtW >= 18f)
                    {
                        var tex = LoadTex(a.Icon);
                        if (tex != null)
                        {
                            float isz = Math.Min(Math.Min(dealtW - 2f, bh - 2f), 40f);
                            var ic = NextIcon(ref iconCur);
                            ic.Texture = tex;
                            ic.SetSize(new Vector2(isz, isz));
                            ic.SetPosition(new Vector2(dealtX + (dealtW - isz) * 0.5f, yTop - bh + (bh - isz) * 0.5f));
                        }
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

            var rl = Next(ref cur, 14, new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.6f));
            rl.Text = row.Round.ToString();
            rl.SetPosition(new Vector2(groupX + groupW * 0.5f - 4f, plotBottom + 4f));
        }
    }

    private void RenderLog(ref int cur, LogEntry[] log, bool full, float pad, float top)
    {
        float leftX = full ? pad : 14f;
        var head = Next(ref cur, full ? 16 : 13, UiTheme.Gold);
        head.Text = "Combat Log";
        head.SetPosition(new Vector2(leftX, top));

        float listTop = top + (full ? 28f * _fontScale : 22f);
        // _bottomReserve already measures the full distance from the screen bottom to keep clear of the
        // close button (don't also subtract pad — that double-counted and wasted ~a pad of log height).
        float listBottom = _h - (full ? _bottomReserve : 10f);
        float lineH = full ? 22f * _fontScale : 16f;
        float regionH = listBottom - listTop;
        _visible = Math.Max(1, (int)(regionH / lineH));

        // Flatten to display rows (round separators + lines), oldest-first.
        var sepColor = new Color(UiTheme.Gold.R, UiTheme.Gold.G, UiTheme.Gold.B, 0.6f);
        var rows = new List<(string Text, Color Color, bool Sep)>();
        int lastRound = int.MinValue;
        foreach (var e in log)
        {
            if (e.Round != lastRound) { rows.Add(($"──── Round {e.Round} ────", sepColor, true)); lastRound = e.Round; }
            rows.Add((e.Text, e.Taken ? UiTheme.Red : UiTheme.Cream, false));
        }

        _maxOffset = Math.Max(0, rows.Count - _visible);
        if (log.Length != _lastLogCount) { if (_logAtBottom) _logOffset = _maxOffset; _lastLogCount = log.Length; } // follow newest
        _logOffset = Math.Clamp(_logOffset, 0, _maxOffset);
        _logAtBottom = _logOffset >= _maxOffset;

        int sepSize = full ? 15 : 10;
        int lineSize = full ? 16 : 11;
        float y = listTop;
        for (int i = _logOffset; i < rows.Count && i < _logOffset + _visible; i++)
        {
            var r = rows[i];
            var lbl = Next(ref cur, r.Sep ? sepSize : lineSize, r.Color);
            lbl.Text = r.Text;
            lbl.SetPosition(new Vector2(r.Sep ? leftX : leftX + 4f, y));
            y += lineH;
        }

        // Custom scrollbar on the right edge.
        _trackY = listTop; _trackH = regionH;
        _sbX = _w - (full ? pad : 12f);
        float sbW = full ? 8f : 6f;
        if (_maxOffset > 0)
        {
            _grabberH = Math.Max(24f, _trackH * _visible / rows.Count);
            _grabberY = _trackY + (_trackH - _grabberH) * ((float)_logOffset / _maxOffset);
            _track.Visible = true; _track.SetPosition(new Vector2(_sbX, _trackY)); _track.SetSize(new Vector2(sbW, _trackH));
            _grabber.Visible = true; _grabber.SetPosition(new Vector2(_sbX, _grabberY)); _grabber.SetSize(new Vector2(sbW, _grabberH));
        }
        else { _track.Visible = false; _grabber.Visible = false; _grabberH = 0f; }
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

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    // In the full-screen takeover, force a content color fully opaque (only the backdrop stays dim).
    private Color Opaque(Color c) => _opaque ? new Color(c.R, c.G, c.B, 1f) : c;

    private Label Next(ref int cursor, int fontSize, Color color)
    {
        if (_opaque) color = new Color(color.R, color.G, color.B, 1f);
        int fs = Math.Max(1, (int)Math.Round(fontSize * _fontScale));
        Label lbl;
        if (cursor < _labels.Count) lbl = _labels[cursor];
        else { lbl = UiTheme.MakeLabel(fs, color); _container.AddChild(lbl); _labels.Add(lbl); }
        UiTheme.Apply(lbl, fs, color);
        lbl.Visible = true;
        cursor++;
        return lbl;
    }
}
