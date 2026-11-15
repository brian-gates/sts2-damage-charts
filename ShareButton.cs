using System;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace STS2_DamageCharts;

// The "Share run" button on the victory and death screens (NGameOverScreen), with a small message panel above
// it. Pressing it reads Spire Sync's status file and does what ShareAction says: open spiresync://share/<run_id>
// and copy <server>/runs/<run_id> (a short note), or explain how to get there (a panel that stays until closed
// or Share is pressed again). The mod itself makes no network calls.
//
// Placement (ShareLayout): beside the game's own button row, bottom-centre (Continue, later Main Menu or
// Leaderboard), read each frame from those buttons' resting rects, never over them or over the run summary or
// leaderboard panel. It is a filled gold button with dark text that glows gently for its first few seconds.
//
// Like the other views it sits on its own high CanvasLayer and reads the mouse by polling (press and release
// over the same target is a click), since GUI input doesn't reliably reach controls on these layers. Its
// controls still stop the mouse when GUI input does arrive, so a click doesn't fall through to the screen.
// Never throws; nothing here allocates per frame once a screen is bound and styled.
internal sealed class ShareButton
{
    private const double NoteSeconds = 6.0;
    private const ulong PollMs = 1000;    // how often the status file is re-read while a share is followed

    private static readonly Color Ink = new(0.13f, 0.08f, 0.02f);               // label on gold
    private static readonly Color FaceIdle = UiTheme.BrandGold;
    private static readonly Color FaceHover = new(1f, 0.80f, 0.36f);
    private static readonly Color FacePressed = new(0.84f, 0.60f, 0.12f);
    private static readonly Color Rim = new(0.42f, 0.25f, 0.03f);
    private static readonly Color RimHover = UiTheme.Cream;

    // The game's buttons, by unique name in game_over_screen.tscn. The first is where the row starts out.
    private static readonly string[] AnchorNames = { "%ContinueButton", "%MainMenuButton", "%LeaderboardButton", "%ViewRunButton" };

    private readonly CanvasLayer _layer;
    private readonly Control _root;
    private readonly Panel _glow;
    private readonly Panel _btn;
    private readonly Label _btnLabel;
    private readonly Control _icon;   // the share glyph left of the label, drawn on its Draw signal
    private const string ButtonText = "Share run";
    private float _iconSize, _iconGap, _textW;   // set on a scale change (StyleButton)
    private readonly StyleBoxFlat _sIdle, _sHover, _sPressed, _sGlow;
    private readonly Panel _msg;
    private readonly Label _msgText;
    private readonly Panel _get;
    private readonly Label _getLabel;
    private readonly Label _close;

    // The bound screen: its buttons with their resting positions (in the screen's own coordinates), and the
    // content the button must stay off.
    private Control? _screen;
    private readonly Control?[] _anchors = new Control?[AnchorNames.Length];
    private readonly Vector2[] _rest = new Vector2[AnchorNames.Length];   // resting position
    private readonly Vector2[] _restSize = new Vector2[AnchorNames.Length]; // size at rest (hover grows it)
    private readonly bool[] _settled = new bool[AnchorNames.Length];        // re-read once it was first visible
    private Control? _shake;         // ScreenshakeContainer: its offset is the screen shake
    private Control? _summary;
    private Control? _board;
    private Box _lastRow;            // the row as last seen, while none of its buttons is visible
    private int _spot = ShareLayout.NoSpot;   // which side of the row the button is on (kept while it fits)

    // The game buttons' private `_showPosition` field, looked up once per button type (null = none).
    private static readonly System.Collections.Generic.Dictionary<Type, FieldInfo?> _showField = new();

    private string _runId = "";
    private string? _message;        // the panel's text; null = no panel
    private bool _showGetApp;
    private bool _closable;
    private ulong _noteUntilMs;      // a transient note hides itself at this time (0 = stays)
    // Following a share after a press (ShareAction.Follow): until Done, re-read the status file every PollMs.
    private bool _following;
    private long _pressedAtUnix;     // the press, unix seconds (floored): older share entries are ignored
    private ulong _pressedAtMs;
    private ulong _nextPollMs;
    private bool _shareReported;     // some read since the press saw a `share` key (an app that reports)
    private ulong _shownAtMs;        // when the button first appeared on this screen (0 = not yet)
    private bool _downLast;
    private Target _armed;           // what the mouse went down on
    private Target _over;            // what the mouse is over, as of the last frame

    private float _styledScale = -1f;      // the scale the button was last styled for
    private float _msgStyledScale = -1f;
    private string? _msgStyledFor;
    private bool _closeLit;

    private enum Target { None, Share, GetApp, Close }

    public ShareButton(Node root)
    {
        _layer = new CanvasLayer { Layer = 130, Name = "Sts2DamageChartsShare", Visible = false };
        root.AddChild(_layer);
        _root = new Control { Name = "Share", MouseFilter = Control.MouseFilterEnum.Ignore };
        _layer.AddChild(_root);

        _sIdle = new StyleBoxFlat();
        _sHover = new StyleBoxFlat();
        _sPressed = new StyleBoxFlat();
        _sGlow = new StyleBoxFlat { DrawCenter = false };

        _glow = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _glow.AddThemeStyleboxOverride("panel", _sGlow);
        _root.AddChild(_glow);

        _btn = new Panel { MouseFilter = Control.MouseFilterEnum.Stop };
        Th.Stylebox(_btn, "panel", _sIdle);
        _root.AddChild(_btn);
        _btnLabel = UiTheme.MakeLabel(26, Ink);
        _btnLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _btnLabel.VerticalAlignment = VerticalAlignment.Center;
        _btnLabel.Text = ButtonText;
        _btn.AddChild(_btnLabel);
        // A plain Control drawn from its Draw signal (the mod has no Godot source generators, so a _Draw
        // override on a subclass would not be called). Godot only redraws it on QueueRedraw or a resize.
        _icon = new Control { Name = "ShareIcon", MouseFilter = Control.MouseFilterEnum.Ignore };
        _icon.Draw += DrawIcon;
        _btn.AddChild(_icon);

        _msg = UiTheme.MakePanel(0.92f, 1f);
        _msg.MouseFilter = Control.MouseFilterEnum.Stop;
        _msg.Visible = false;
        _root.AddChild(_msg);
        _msgText = UiTheme.MakeLabel(20, UiTheme.Cream);
        _msgText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _msg.AddChild(_msgText);
        _close = UiTheme.MakeLabel(20, UiTheme.Gray);
        _close.Text = "✕";
        _msg.AddChild(_close);
        _get = UiTheme.MakePanel(0.9f, 1f);
        _get.MouseFilter = Control.MouseFilterEnum.Ignore;
        _msg.AddChild(_get);
        _getLabel = UiTheme.MakeLabel(20, UiTheme.Gold);
        _getLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _getLabel.VerticalAlignment = VerticalAlignment.Center;
        _getLabel.Text = "Get Spire Sync";
        _get.AddChild(_getLabel);
    }

    public bool IsValid() => GodotObject.IsInstanceValid(_layer);
    public void Dispose() { if (IsValid()) _layer.QueueFree(); }

    // A new game-over screen: start without a panel, find its buttons and content, glow again when shown.
    public void Reset(string runId, Node screen)
    {
        _runId = runId;
        _message = null;
        _following = false;
        _armed = Target.None;
        _downLast = true;   // a button still held from the previous screen isn't a press here
        _shownAtMs = 0;
        _lastRow = default;
        _spot = ShareLayout.NoSpot;
        Bind(screen);
    }

    public void Hide()
    {
        if (IsValid() && _layer.Visible) _layer.Visible = false;
        _armed = Target.None;
        _downLast = true;   // a press that started while hidden isn't a click once shown
    }

    // Called every frame while the game-over screen is on top.
    public void Update(bool mouseDown)
    {
        if (!IsValid()) return;
        try
        {
            if (!_layer.Visible) _layer.Visible = true;
            ulong now = Time.GetTicksMsec();
            if (_shownAtMs == 0) _shownAtMs = now;
            if (_noteUntilMs != 0 && now >= _noteUntilMs) { _message = null; _noteUntilMs = 0; }
            if (_following && now >= _nextPollMs) Poll(now);
            Layout(now);

            Vector2 mouse = _root.GetViewport().GetMousePosition();
            Target over = _over = Hit(mouse);
            Th.Stylebox(_btn, "panel", over != Target.Share ? _sIdle : mouseDown && _armed == Target.Share ? _sPressed : _sHover);
            var lit = new Color(1.25f, 1.25f, 1.25f);
            if (_get.SelfModulate != (over == Target.GetApp ? lit : Colors.White)) _get.SelfModulate = over == Target.GetApp ? lit : Colors.White;

            if (mouseDown && !_downLast) _armed = over;
            else if (!mouseDown && _downLast)
            {
                if (_armed != Target.None && _armed == over) Press(_armed);
                _armed = Target.None;
            }
            _downLast = mouseDown;
        }
        catch (Exception ex) { ErrorLog.Once("share button", ex); }
    }

    // True while the button (or its panel) is on screen under `p`: the mod keeps the game's own buttons from
    // reacting to that press.
    public bool Covers(Vector2 p)
    {
        if (!IsValid() || !_layer.Visible) return false;
        return _btn.GetGlobalRect().HasPoint(p) || (_msg.Visible && _msg.GetGlobalRect().HasPoint(p));
    }

    private Target Hit(Vector2 p)
    {
        if (_msg.Visible)
        {
            if (_get.Visible && _get.GetGlobalRect().HasPoint(p)) return Target.GetApp;
            if (_close.Visible && _close.GetGlobalRect().Grow(6f).HasPoint(p)) return Target.Close;
        }
        return _btn.GetGlobalRect().HasPoint(p) ? Target.Share : Target.None;
    }

    private void Press(Target t)
    {
        switch (t)
        {
            case Target.Share: Share(); break;
            case Target.GetApp: OS.ShellOpen(ShareAction.DownloadUrl); break;
            case Target.Close: _message = null; _following = false; break;
        }
    }

    private void Share()
    {
        _following = false;
        var status = ShareAction.ReadStatusFile(AppStatus.FilePath(), DateTimeOffset.UtcNow);
        var action = ShareAction.For(status, _runId);
        GD.Print($"[STS2 Damage] share {_runId}: Spire Sync {status.State}");
        if (action.Kind != ShareKind.Share)
        {
            Show(action.Message, action.ShowGetApp, transient: false);
            return;
        }
        // Copy only once the app has been handed the run: a link to a run that isn't shared would not open.
        var err = OS.ShellOpen(action.AppLink!);
        if (err != Error.Ok)
        {
            GD.PrintErr($"[STS2 Damage] share: opening Spire Sync failed ({err})");
            Show(ShareAction.OpenFailedText, getApp: false, transient: false);
            return;
        }
        DisplayServer.ClipboardSet(action.WebLink!);
        // Keep the note up and follow the app's progress for this run (Poll).
        Show(action.Message, getApp: false, transient: false);
        _following = true;
        _shareReported = false;
        _pressedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _pressedAtMs = Time.GetTicksMsec();
        _nextPollMs = _pressedAtMs + PollMs;
    }

    // Once a second while following: re-read the status file and update the note. Never throws; a failure stops
    // following and leaves the note as it was.
    private void Poll(ulong now)
    {
        _nextPollMs = now + PollMs;
        try
        {
            var status = ShareAction.ReadStatusFile(AppStatus.FilePath(), DateTimeOffset.UtcNow);
            _shareReported |= status.ShareReported;
            var p = ShareAction.Follow(status.Share, _shareReported, _runId, _pressedAtUnix, (now - _pressedAtMs) / 1000.0);
            if (!ReferenceEquals(p.Message, _message) || p.Done)
            {
                if (p.Done) GD.Print($"[STS2 Damage] share {_runId}: {(status.Share?.RunId == _runId ? status.Share.State.ToString() : "no confirmation")}");
                Show(p.Message, getApp: false, transient: p.AutoHide);
            }
            if (p.Done) _following = false;
        }
        catch (Exception ex) { _following = false; ErrorLog.Once("share progress", ex); }
    }

    private void Show(string text, bool getApp, bool transient)
    {
        _message = text;
        _showGetApp = getApp;
        _closable = !transient;
        _noteUntilMs = transient ? Time.GetTicksMsec() + (ulong)(NoteSeconds * 1000) : 0;
    }

    // ----- The screen's controls -----

    // Once per screen (allocates: node paths). Each button's resting rect is the game's own `_showPosition`
    // with the size it has then (its buttons slide in and out and grow on hover, so the live rect moves);
    // without that field, its position now. Read here and again once the button is first visible, in case the
    // game only sets it when the button is shown.
    private void Bind(Node screen)
    {
        _screen = null; _shake = null; _summary = null; _board = null;
        Array.Clear(_anchors);
        Array.Clear(_settled);
        try
        {
            if (screen is not Control sc) return;
            _screen = sc;
            for (int i = 0; i < AnchorNames.Length; i++)
            {
                if (sc.GetNodeOrNull(AnchorNames[i]) is not Control c) continue;
                _anchors[i] = c;
                ReadRest(i, c);
            }
            _shake = sc.GetNodeOrNull("%ScreenshakeContainer") as Control;
            // The summary's content is the MarginContainer's VBox (the container itself has 250 px side margins);
            // the leaderboard's visible part is its Panel (the control itself runs off the bottom of the screen).
            var summary = sc.GetNodeOrNull("%RunSummaryContainer");
            _summary = summary != null && summary.GetChildCount() > 0 ? summary.GetChild(0) as Control : null;
            _board = sc.GetNodeOrNull("%DailyRunLeaderboard/Panel") as Control;
            int found = 0;
            foreach (var a in _anchors) if (a != null) found++;
            if (found == 0) ErrorLog.NoteOnce("share: game-over buttons not found (game changed?): Share button sits bottom-centre right");
        }
        catch (Exception ex) { ErrorLog.Once("share bind", ex); }
    }

    // A button's resting position and size (allocates: one boxed read; at most twice per button per screen).
    private void ReadRest(int i, Control c)
    {
        _rest[i] = c.Position;
        _restSize[i] = c.Size;
        try
        {
            var t = c.GetType();
            if (!_showField.TryGetValue(t, out var f)) _showField[t] = f = AccessTools.Field(t, "_showPosition");
            if (f != null && f.GetValue(c) is Vector2 v && v != Vector2.Zero) _rest[i] = v;
        }
        catch { }
    }

    // The game's button row now, in viewport pixels: the visible buttons at rest; else the row as last seen
    // (between the screen's two pages none is visible); else where Continue will appear; else empty.
    private Box Row(Transform2D xf)
    {
        if (_screen == null) return _lastRow;
        Box row = default, first = default;
        for (int i = 0; i < _anchors.Length; i++)
        {
            var c = _anchors[i];
            if (c == null || !GodotObject.IsInstanceValid(c)) continue;
            bool visible = c.IsVisibleInTree();
            if (visible && !_settled[i]) { ReadRest(i, c); _settled[i] = true; }
            Vector2 o = xf * _rest[i];
            Vector2 s = _restSize[i] * xf.Scale;
            var b = new Box(o.X, o.Y, s.X, s.Y);
            if (first.IsEmpty) first = b;
            if (visible) row = row.Union(b);
        }
        if (!row.IsEmpty) return _lastRow = row;
        return _lastRow.IsEmpty ? first : _lastRow;
    }

    // The screen's transform, which the shake doesn't move: the buttons sit in Ui, and the content in
    // ScreenshakeContainer's other children, both of which fill the screen at offset 0.
    private bool ScreenTransform(out Transform2D xf)
    {
        xf = Transform2D.Identity;
        if (_screen == null || !GodotObject.IsInstanceValid(_screen)) return false;
        xf = _screen.GetGlobalTransformWithCanvas();
        return true;
    }

    // A content control's rect with the shake taken out: its transform relative to ScreenshakeContainer, mapped
    // through the screen's transform.
    private Box Content(Control? c, Transform2D screen)
    {
        if (c == null || !GodotObject.IsInstanceValid(c) || !c.IsVisibleInTree()) return default;
        Transform2D xf = c.GetGlobalTransformWithCanvas();
        if (_shake != null && GodotObject.IsInstanceValid(_shake))
            xf = screen * (_shake.GetGlobalTransformWithCanvas().AffineInverse() * xf);
        Vector2 s = c.Size * xf.Scale;
        return new Box(xf.Origin.X, xf.Origin.Y, s.X, s.Y);
    }

    // ----- Layout and style -----

    private void Layout(ulong now)
    {
        Vector2 vp;
        try { vp = _root.GetViewportRect().Size; } catch { vp = new Vector2(1920, 1080); }
        float sc = UiTheme.Scale(vp);
        float pad = 12f * sc;

        if (sc != _styledScale) { StyleButton(sc); _styledScale = sc; }

        Box row = default, summary = default, board = default;
        if (ScreenTransform(out var screen))
        {
            row = Row(screen);
            summary = Content(_summary, screen);
            board = Content(_board, screen);
        }
        else row = _lastRow;
        Box at = ShareLayout.Place(row, summary, board, vp.X, vp.Y, sc, ref _spot);
        var btnPos = new Vector2(at.X, at.Y);
        var btnSize = new Vector2(at.W, at.H);
        if (_btn.Position != btnPos) _btn.Position = btnPos;
        if (_btn.Size != btnSize) _btn.Size = btnSize;
        if (_btnLabel.Size != btnSize) _btnLabel.Size = btnSize;
        // Glyph + label centred as a pair: the centre-aligned label shifts right by half the glyph and gap.
        var (ix, iy, shift) = ShareLayout.IconRow(btnSize.X, btnSize.Y, _iconSize, _iconGap, _textW);
        var labelPos = new Vector2(shift, 0f);
        if (_btnLabel.Position != labelPos) _btnLabel.Position = labelPos;
        bool iconOn = _iconSize > 0f;
        if (_icon.Visible != iconOn) _icon.Visible = iconOn;
        var iconPos = new Vector2(ix, iy);
        var iconSize = new Vector2(_iconSize, _iconSize);
        if (_icon.Position != iconPos) _icon.Position = iconPos;
        if (_icon.Size != iconSize) _icon.Size = iconSize;

        // The first-appearance glow: a gold halo that pulses, fades, then hides for good on this screen.
        float glow = ShareLayout.Glow(now - _shownAtMs);
        bool glowOn = glow > 0.001f;
        if (_glow.Visible != glowOn) _glow.Visible = glowOn;
        if (glowOn)
        {
            if (_glow.Position != btnPos) _glow.Position = btnPos;
            if (_glow.Size != btnSize) _glow.Size = btnSize;
            var m = new Color(1f, 1f, 1f, glow);
            if (_glow.Modulate != m) _glow.Modulate = m;
        }

        bool showMsg = _message != null;
        if (_msg.Visible != showMsg) _msg.Visible = showMsg;
        if (!showMsg) return;

        int fs = (int)Math.Round(20 * sc);
        if (sc != _msgStyledScale)
        {
            UiTheme.RestylePanel(_msg, 0.92f, sc);
            UiTheme.RestylePanel(_get, 0.9f, sc);
            UiTheme.Apply(_msgText, fs, UiTheme.Cream);
            UiTheme.Apply(_getLabel, fs, UiTheme.Gold);
            _msgStyledScale = sc;
            _msgStyledFor = null;
        }
        bool closeLit = _over == Target.Close;
        if (_msgStyledFor == null || closeLit != _closeLit)
        {
            UiTheme.Apply(_close, fs, closeLit ? UiTheme.Cream : UiTheme.Gray);
            _closeLit = closeLit;
        }
        if (!ReferenceEquals(_msgStyledFor, _message)) { Th.Text(_msgText, _message!); _msgStyledFor = _message; }

        float w = 420f * sc;
        float closeW = _closable ? fs + pad : 0f;
        float textW = w - 2 * pad - closeW;
        // Width first: the wrapped line count depends on it. Sizes are only written when they change.
        if (_msgText.Size.X != textW) _msgText.Size = new Vector2(textW, _msgText.Size.Y);
        float textH = Math.Max(1, _msgText.GetLineCount()) * _msgText.GetLineHeight();
        var textSize = new Vector2(textW, textH);
        if (_msgText.Size != textSize) _msgText.Size = textSize;
        if (_msgText.Position != new Vector2(pad, pad)) _msgText.Position = new Vector2(pad, pad);

        if (_close.Visible != _closable) _close.Visible = _closable;
        var closePos = new Vector2(w - pad - fs, pad * 0.5f);
        if (_closable && _close.Position != closePos) _close.Position = closePos;

        float h = pad + textH + pad;
        if (_get.Visible != _showGetApp) _get.Visible = _showGetApp;
        if (_showGetApp)
        {
            var getSize = new Vector2(190f * sc, 42f * sc);
            var getPos = new Vector2(pad, h);
            if (_get.Position != getPos) _get.Position = getPos;
            if (_get.Size != getSize) _get.Size = getSize;
            if (_getLabel.Size != getSize) _getLabel.Size = getSize;
            h += getSize.Y + pad;
        }
        var msgSize = new Vector2(w, h);
        if (_msg.Size != msgSize) _msg.Size = msgSize;
        // Above the button, never over it or the game's buttons where avoidable (ShareLayout.PlacePanel).
        var panel = ShareLayout.PlacePanel(at, row, w, h, vp.X, vp.Y, sc);
        var msgPos = new Vector2(panel.X, panel.Y);
        if (_msg.Position != msgPos) _msg.Position = msgPos;
    }

    // On a scale change only: the gold face in three states, the halo, and the label.
    private void StyleButton(float sc)
    {
        try
        {
            int radius = (int)Math.Round(10 * sc);
            int border = Math.Max(1, (int)Math.Round(2 * sc));
            Face(_sIdle, FaceIdle, Rim, radius, border, sc);
            Face(_sHover, FaceHover, RimHover, radius, border, sc);
            Face(_sPressed, FacePressed, Rim, radius, border, sc);
            _sGlow.SetCornerRadiusAll(radius);
            _sGlow.ShadowColor = new Color(UiTheme.BrandGold, 0.85f);
            _sGlow.ShadowSize = Math.Max(1, (int)Math.Round(22 * sc));
            int fs = (int)Math.Round(26 * sc);
            var font = GameAssets.KreonBold ?? UiTheme.DefaultFont;
            UiTheme.Apply(_btnLabel, fs, Ink, font, Colors.Transparent, 0);
            _iconSize = (float)Math.Round(ShareLayout.IconSize(fs));
            _iconGap = (float)Math.Round(fs * 0.35f);
            _textW = font != null ? font.GetStringSize(ButtonText, HorizontalAlignment.Left, -1f, fs).X : fs * 0.5f * ButtonText.Length;
            _icon.QueueRedraw();
        }
        catch (Exception ex) { ErrorLog.Once("share style", ex); }
    }

    // Drawn only when Godot asks (QueueRedraw on a scale change, or a resize): two lines, then three nodes.
    private void DrawIcon()
    {
        try
        {
            var g = ShareLayout.ShareGlyph(_icon.Size.X);
            if (g.Radius <= 0f) return;
            var l = new Vector2(g.LX, g.LY);
            var t = new Vector2(g.TX, g.TY);
            var b = new Vector2(g.BX, g.BY);
            _icon.DrawLine(l, t, Ink, g.Line, true);
            _icon.DrawLine(l, b, Ink, g.Line, true);
            _icon.DrawCircle(l, g.Radius, Ink, true, -1f, true);
            _icon.DrawCircle(t, g.Radius, Ink, true, -1f, true);
            _icon.DrawCircle(b, g.Radius, Ink, true, -1f, true);
        }
        catch (Exception ex) { ErrorLog.Once("share icon", ex); }
    }

    private static void Face(StyleBoxFlat sb, Color face, Color rim, int radius, int border, float sc)
    {
        sb.BgColor = face;
        sb.BorderColor = rim;
        sb.SetBorderWidthAll(border);
        sb.SetCornerRadiusAll(radius);
        sb.ShadowColor = new Color(0f, 0f, 0f, 0.55f);
        sb.ShadowSize = Math.Max(1, (int)Math.Round(6 * sc));
        sb.ShadowOffset = new Vector2(0f, 3f * sc);
        sb.AntiAliasing = true;
    }
}
