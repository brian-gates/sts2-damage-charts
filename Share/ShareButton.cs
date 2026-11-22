using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using STS2_DamageCharts.Common;
using STS2_DamageCharts.Forecast;
using STS2_DamageCharts.Ui;

namespace STS2_DamageCharts.Share;

// The "Share run" button on the victory and death screens (NGameOverScreen), with a small message panel above
// it. Pressing it reads Spire Sync's status file and does what ShareAction says: open spiresync://share/<run_id>
// and copy <server>/runs/<run_id> (a short note), or explain how to get there (a panel that stays until closed
// or Share is pressed again). The mod itself makes no network calls.
//
// "Share fight" (SetFight) shares one fight of the run: it sits next to Share run on the game-over screen (the
// run's last written fight) and, in the Summary spot, alone under the end-of-combat summary panel (Update's
// anchor). It opens spiresync://share/<run_id>?fight=<index>&floor=<floor> and copies the fight's page only
// once the app reports that the server has it (ShareAction.FollowFight); unlike a run share, that wait goes on
// while the button is hidden, so the link still arrives after the player moves on.
//
// Placement (ShareLayout): beside the game's own button row, bottom-centre: the union of the resting rects of
// all its row buttons (Continue, Leaderboard, Main Menu, View Run), shown or not, so the spot stays put across
// the screen's pages. Never over them or over the run summary or leaderboard panel. It is a filled gold button with dark text that glows gently for its first few seconds.
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

    private const float SummaryMul = 0.75f;   // the Summary spot's button is smaller than the game-over ones

    // One gold button: its halo, face, label and share glyph (drawn on the glyph's Draw signal).
    private sealed class Face
    {
        public readonly Panel Glow, Btn;
        public readonly Label Label;
        public readonly Control Icon;
        public readonly string Text;
        public float IconSize, IconGap, TextW;   // set on a scale change (StyleButton)
        public ulong ShownAtMs;                  // when it first appeared on this screen (0 = not yet)

        public Face(Control root, string text, StyleBoxFlat glow, StyleBoxFlat idle)
        {
            Text = text;
            Glow = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
            Glow.AddThemeStyleboxOverride("panel", glow);
            root.AddChild(Glow);
            Btn = new Panel { MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
            Th.Stylebox(Btn, "panel", idle);
            root.AddChild(Btn);
            Label = UiTheme.MakeLabel(26, Ink);
            Label.HorizontalAlignment = HorizontalAlignment.Center;
            Label.VerticalAlignment = VerticalAlignment.Center;
            Label.Text = text;
            Btn.AddChild(Label);
            // A plain Control drawn from its Draw signal (the mod has no Godot source generators, so a _Draw
            // override on a subclass would not be called). Godot only redraws it on QueueRedraw or a resize.
            Icon = new Control { Name = "ShareIcon", MouseFilter = Control.MouseFilterEnum.Ignore };
            Icon.Draw += () => DrawIcon(Icon);
            Btn.AddChild(Icon);
        }

        public bool Covers(Vector2 p) => Btn.Visible && Btn.GetGlobalRect().HasPoint(p);

        public void Hide()
        {
            if (Btn.Visible) Btn.Visible = false;
            if (Glow.Visible) Glow.Visible = false;
            ShownAtMs = 0;
        }
    }

    private readonly ShareSpot _spot0;   // where this instance lives
    private readonly CanvasLayer _layer;
    private readonly Control _root;
    private readonly Face? _run;         // Share run: game-over screen only
    private readonly Face _fight;        // Share fight: shown while a fight is set
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
    private Control? _shake;         // ScreenshakeContainer: its offset is the screen shake
    private Control? _summary;
    private Control? _board;
    private readonly Box[] _restBoxes = new Box[AnchorNames.Length];
    // The row, computed once per screen, viewport size and UI scale (never on a button showing or hiding).
    private Box _row;
    private bool _rowValid;
    private Vector2 _rowVp;
    private float _rowScale;
    private Transform2D _rowXf;      // the screen transform the row was computed with (shake excluded)
    private readonly bool[] _restOk = new bool[AnchorNames.Length];   // a real _showPosition and size were read
    private RowFreeze _freeze;       // until frozen the rests are re-read and the button stays hidden
    private ulong _boundAtMs;
    private int _spot = ShareLayout.NoSpot;   // which side of the row the button is on (kept while it fits)

    // The game buttons' private `_showPosition` field, looked up once per button type (null = none).
    private static readonly System.Collections.Generic.Dictionary<Type, FieldInfo?> _showField = new();

    private string _runId = "";
    private int? _fightIndex;        // the fight Share fight shares (null = no Share fight button)
    private int? _fightFloor;
    private Box _anchor;             // Summary spot: the summary panel's frame, in viewport pixels
    private string? _message;        // the panel's text; null = no panel
    private bool _showGetApp;
    private bool _closable;
    private ulong _noteUntilMs;      // a transient note hides itself at this time (0 = stays)
    // Following a share after a press (ShareAction.Follow / FollowFight): until Done, re-read the status file
    // every PollMs.
    private bool _following;
    private long _followToken;       // this button's press in ShareFollow: a newer press anywhere ends the follow
    private bool _followFight;       // the share followed is a fight's (_followIndex), whose link is copied at the end
    private int _followIndex;
    private string? _followLink;
    private DateTimeOffset _pressedAt;
    private long _pressedAtUnix;     // the press, unix seconds (floored): older share entries are ignored
    private ulong _pressedAtMs;      // moved forward by time spent hidden, so the clock pauses while hidden
    private ulong _hiddenAtMs;       // when the button was hidden while following a run share (0 = shown)
    private ulong _nextPollMs;
    private bool _shareReported;     // some read since the press saw a `share` key (an app that reports)
    private bool _downLast;
    private Target _armed;           // what the mouse went down on
    private Target _over;            // what the mouse is over, as of the last frame

    private float _styledScale = -1f;      // the scale the button was last styled for
    private float _msgStyledScale = -1f;
    private string? _msgStyledFor;
    private bool _closeLit;

    private enum Target { None, Run, Fight, GetApp, Close }

    public ShareButton(Node root, ShareSpot spot = ShareSpot.GameOver)
    {
        _spot0 = spot;
        _layer = new CanvasLayer { Layer = 130, Name = spot == ShareSpot.GameOver ? "Sts2DamageChartsShare" : "Sts2DamageChartsShareFight", Visible = false };
        root.AddChild(_layer);
        _root = new Control { Name = "Share", MouseFilter = Control.MouseFilterEnum.Ignore };
        _layer.AddChild(_root);

        _sIdle = new StyleBoxFlat();
        _sHover = new StyleBoxFlat();
        _sPressed = new StyleBoxFlat();
        _sGlow = new StyleBoxFlat { DrawCenter = false };

        if (spot == ShareSpot.GameOver) _run = new Face(_root, "Share run", _sGlow, _sIdle);
        _fight = new Face(_root, "Share fight", _sGlow, _sIdle);

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

    // A new game-over screen (or, in the Summary spot, a new fight's summary: screen null): start without a
    // panel, find the screen's buttons and content, glow again when shown. Stops following an earlier press.
    public void Reset(string runId, Node? screen)
    {
        _runId = runId;
        _message = null;
        StopFollowing();
        _hiddenAtMs = 0;
        _armed = Target.None;
        _downLast = true;   // a button still held from the previous screen isn't a press here
        _run?.Hide();
        _fight.Hide();
        _row = default;
        _rowValid = false;
        _freeze = default;
        _boundAtMs = Time.GetTicksMsec();
        _spot = ShareLayout.NoSpot;
        if (screen != null) Bind(screen);
    }

    // The fight Share fight shares (index and floor in the run stats file), or null for no Share fight button.
    // Set each frame; it doesn't touch a share already being followed.
    public void SetFight(int? index, int? floor)
    {
        _fightIndex = index;
        _fightFloor = index != null ? floor : null;
    }

    // Called every frame while not shown. A fight share is still followed (its link is copied when the app
    // confirms it); a run share's follow clock pauses until the button is back.
    public void Hide()
    {
        if (IsValid() && _layer.Visible) _layer.Visible = false;
        if (_following && !_followFight && _hiddenAtMs == 0) _hiddenAtMs = Time.GetTicksMsec();
        _armed = Target.None;
        _downLast = true;   // a press that started while hidden isn't a click once shown
        if (_following && !ShareFollow.IsCurrent(_followToken)) Superseded();
        if (_following && _followFight && IsValid())
        {
            try { ulong now = Time.GetTicksMsec(); if (now >= _nextPollMs) Poll(now); }
            catch (Exception ex) { ErrorLog.Once("share fight progress", ex); }
        }
    }

    // Called every frame while the game-over screen (or, in the Summary spot, the summary) is on top. `anchor`:
    // the summary panel's frame in viewport pixels (Summary spot only).
    public void Update(bool mouseDown, Box anchor = default)
    {
        if (!IsValid()) return;
        try
        {
            if (!_layer.Visible) _layer.Visible = true;
            _anchor = anchor;
            ulong now = Time.GetTicksMsec();
            if (_hiddenAtMs != 0)
            {
                // Back from hiding (a menu or run history was on top): the follow clock didn't run meanwhile.
                ulong away = now > _hiddenAtMs ? now - _hiddenAtMs : 0;
                _pressedAtMs += away;
                if (_noteUntilMs != 0) _noteUntilMs += away;
                _hiddenAtMs = 0;
            }
            if (_noteUntilMs != 0 && now >= _noteUntilMs) { _message = null; _noteUntilMs = 0; }
            if (_following && !ShareFollow.IsCurrent(_followToken)) Superseded();
            if (_following && now >= _nextPollMs) Poll(now);
            Layout(now);

            Vector2 mouse = _root.GetViewport().GetMousePosition();
            Target over = _over = Hit(mouse);
            if (_run != null) Th.Stylebox(_run.Btn, "panel", FaceStyle(Target.Run, over, mouseDown));
            Th.Stylebox(_fight.Btn, "panel", FaceStyle(Target.Fight, over, mouseDown));
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

    private StyleBoxFlat FaceStyle(Target face, Target over, bool mouseDown) =>
        over != face ? _sIdle : mouseDown && _armed == face ? _sPressed : _sHover;

    // True while a button (or the panel) is on screen under `p`: the mod keeps the game's own buttons from
    // reacting to that press.
    public bool Covers(Vector2 p)
    {
        if (!IsValid() || !_layer.Visible) return false;
        return (_run?.Covers(p) ?? false) || _fight.Covers(p) || (_msg.Visible && _msg.GetGlobalRect().HasPoint(p));
    }

    private Target Hit(Vector2 p)
    {
        if (_msg.Visible)
        {
            if (_get.Visible && _get.GetGlobalRect().HasPoint(p)) return Target.GetApp;
            if (_close.Visible && _close.GetGlobalRect().Grow(6f).HasPoint(p)) return Target.Close;
        }
        if (_run != null && _run.Covers(p)) return Target.Run;
        return _fight.Covers(p) ? Target.Fight : Target.None;
    }

    private void Press(Target t)
    {
        switch (t)
        {
            case Target.Run: ShareRun(); break;
            case Target.Fight: ShareFight(); break;
            case Target.GetApp: OS.ShellOpen(ShareAction.DownloadUrl); break;
            case Target.Close: _message = null; StopFollowing(); break;
        }
    }

    private void ShareFight()
    {
        if (_fightIndex is not int index) return;
        _following = false;
        _followToken = ShareFollow.Begin();   // this press ends any other button's follow
        var status = ShareAction.ReadStatusFile(AppStatus.FilePath(), DateTimeOffset.UtcNow);
        var action = ShareAction.ForFight(status, _runId, index, _fightFloor);
        GD.Print($"[STS2 Damage] share fight {_runId} #{index}: Spire Sync {status.State}, fights {(status.CanShareFight ? "supported" : "not supported")}");
        if (action.Kind != ShareKind.Share)
        {
            Show(action.Message, action.ShowGetApp, transient: false);
            return;
        }
        // The press time is taken before handing over the fight, so the app's stamp can't predate it.
        var pressedAt = DateTimeOffset.UtcNow;
        ulong pressedAtMs = Time.GetTicksMsec();
        var err = OS.ShellOpen(action.AppLink!);
        if (err != Error.Ok)
        {
            GD.PrintErr($"[STS2 Damage] share fight: opening Spire Sync failed ({err})");
            Show(ShareAction.OpenFailedText, getApp: false, transient: false);
            return;
        }
        // Nothing is copied yet: the fight's page exists only once the server has the fight (Poll copies it).
        Show(action.Message, getApp: false, transient: false);
        _following = true;
        _followFight = true;
        _followIndex = index;
        _followLink = action.WebLink;
        _pressedAt = pressedAt;
        _pressedAtMs = pressedAtMs;
        _hiddenAtMs = 0;
        _nextPollMs = Time.GetTicksMsec() + PollMs;
    }

    private void ShareRun()
    {
        _following = false;
        _followToken = ShareFollow.Begin();   // this press ends any other button's follow
        var status = ShareAction.ReadStatusFile(AppStatus.FilePath(), DateTimeOffset.UtcNow);
        var action = ShareAction.For(status, _runId);
        GD.Print($"[STS2 Damage] share {_runId}: Spire Sync {status.State}");
        if (action.Kind != ShareKind.Share)
        {
            Show(action.Message, action.ShowGetApp, transient: false);
            return;
        }
        // The press time is taken before handing over the run, so the app's stamp can't predate it.
        long pressedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ulong pressedAtMs = Time.GetTicksMsec();
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
        _followFight = false;
        _shareReported = false;
        _pressedAtUnix = pressedAtUnix;
        _pressedAtMs = pressedAtMs;
        _hiddenAtMs = 0;
        _nextPollMs = Time.GetTicksMsec() + PollMs;
    }

    // Once a second while following: re-read the status file and update the note. Never throws; a failure stops
    // following and leaves the note as it was.
    private void Poll(ulong now)
    {
        _nextPollMs = now + PollMs;
        try
        {
            var status = ShareAction.ReadStatusFile(AppStatus.FilePath(), DateTimeOffset.UtcNow);
            double elapsed = (now - _pressedAtMs) / 1000.0;
            ShareAction.Progress p;
            if (_followFight) p = ShareAction.FollowFight(status, _runId, _followIndex, _pressedAt, elapsed);
            else
            {
                _shareReported |= status.ShareReported;
                p = ShareAction.Follow(status.Share, _shareReported, _runId, _pressedAtUnix, elapsed);
            }
            if (!ShareFollow.IsCurrent(_followToken)) { Superseded(); return; }   // a newer press owns the clipboard
            if (p.Copy && _followLink != null) DisplayServer.ClipboardSet(_followLink);
            if (!ReferenceEquals(p.Message, _message) || p.Done)
            {
                if (p.Done)
                    GD.Print($"[STS2 Damage] share {_runId}{(_followFight ? $" fight #{_followIndex}" : "")}: " +
                             $"{(status.Share?.RunId == _runId ? status.Share.State.ToString() : "no confirmation")}");
                Show(p.Message, getApp: false, transient: p.AutoHide);
            }
            if (p.Done) StopFollowing();
        }
        catch (Exception ex) { StopFollowing(); ErrorLog.Once("share progress", ex); }
    }

    private void StopFollowing()
    {
        _following = false;
        ShareFollow.Cancel(_followToken);
    }

    // Another press (on this button or the other one) took over: stop without copying, and drop this note.
    private void Superseded()
    {
        _following = false;
        _message = null;
        _noteUntilMs = 0;
    }

    private void Show(string text, bool getApp, bool transient)
    {
        _message = text;
        _showGetApp = getApp;
        _closable = !transient;
        _noteUntilMs = transient ? Time.GetTicksMsec() + (ulong)(NoteSeconds * 1000) : 0;
    }

    // ----- The screen's controls -----

    // Once per screen (allocates: node paths): finds the row buttons and the content to stay off, and reads
    // each button's resting rect (ReadRest). Until the row is frozen (RowFreeze), Layout re-reads the buttons
    // whose rect isn't real yet; after that nothing is re-read, so the row (and the Share button) never moves
    // when a button shows or hides.
    private void Bind(Node screen)
    {
        _screen = null; _shake = null; _summary = null; _board = null;
        Array.Clear(_anchors);
        Array.Clear(_restOk);
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

    // A button's resting rect: the game's own `_showPosition` (its buttons slide in and out and grow on hover,
    // so the live rect moves) with its size. Without that field, or while it is zero, its position now, and the
    // rect isn't counted as real (`_restOk`). Allocates one boxed read; only until the row is frozen.
    private void ReadRest(int i, Control c)
    {
        _rest[i] = c.Position;
        _restSize[i] = c.Size;
        bool shown = false;
        try
        {
            var t = c.GetType();
            if (!_showField.TryGetValue(t, out var f)) _showField[t] = f = AccessTools.Field(t, "_showPosition");
            if (f != null && f.GetValue(c) is Vector2 v && v != Vector2.Zero) { _rest[i] = v; shown = true; }
        }
        catch { }
        _restOk[i] = shown && _restSize[i].X > 0f && _restSize[i].Y > 0f;
    }

    // Before the freeze: re-read the buttons without a real rect yet. True when every found button has one.
    private bool RefreshRests()
    {
        bool all = true;
        for (int i = 0; i < _anchors.Length; i++)
        {
            var c = _anchors[i];
            if (c == null || !GodotObject.IsInstanceValid(c) || _restOk[i]) continue;
            ReadRest(i, c);
            all &= _restOk[i];
        }
        return all;
    }

    // The game's button row in viewport pixels: the union of every row button's resting rect, visible or not
    // (ShareLayout.Row). Empty when none was found (ShareLayout then uses Continue's spot from the scene).
    private Box RestRow(Transform2D xf)
    {
        for (int i = 0; i < _anchors.Length; i++)
        {
            var c = _anchors[i];
            if (c == null || !GodotObject.IsInstanceValid(c)) { _restBoxes[i] = default; continue; }
            Vector2 o = xf * _rest[i];
            Vector2 s = _restSize[i] * xf.Scale;
            _restBoxes[i] = new Box(o.X, o.Y, s.X, s.Y);
        }
        return ShareLayout.Row(_restBoxes);
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

        Box row = default, buttons;
        if (_spot0 == ShareSpot.GameOver)
        {
            if (!LayoutGameOver(now, vp, sc, out row, out buttons)) return;
        }
        else
        {
            // Under the summary panel; nothing without a fight or a panel to sit by.
            if (_fightIndex == null || _anchor.IsEmpty) { _fight.Hide(); if (_msg.Visible) _msg.Visible = false; return; }
            float w = ShareLayout.ButtonW * SummaryMul * sc, h = ShareLayout.ButtonH * SummaryMul * sc;
            buttons = ShareLayout.PlaceNear(_anchor, _anchor, w, h, vp.X, vp.Y, sc);
            Place(_fight, buttons, now);
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

        float mw = 420f * sc;
        float closeW = _closable ? fs + pad : 0f;
        float textW = mw - 2 * pad - closeW;
        // Width first: the wrapped line count depends on it. Sizes are only written when they change.
        if (_msgText.Size.X != textW) _msgText.Size = new Vector2(textW, _msgText.Size.Y);
        float textH = Math.Max(1, _msgText.GetLineCount()) * _msgText.GetLineHeight();
        var textSize = new Vector2(textW, textH);
        if (_msgText.Size != textSize) _msgText.Size = textSize;
        if (_msgText.Position != new Vector2(pad, pad)) _msgText.Position = new Vector2(pad, pad);

        if (_close.Visible != _closable) _close.Visible = _closable;
        var closePos = new Vector2(mw - pad - fs, pad * 0.5f);
        if (_closable && _close.Position != closePos) _close.Position = closePos;

        float mh = pad + textH + pad;
        if (_get.Visible != _showGetApp) _get.Visible = _showGetApp;
        if (_showGetApp)
        {
            var getSize = new Vector2(190f * sc, 42f * sc);
            var getPos = new Vector2(pad, mh);
            if (_get.Position != getPos) _get.Position = getPos;
            if (_get.Size != getSize) _get.Size = getSize;
            if (_getLabel.Size != getSize) _getLabel.Size = getSize;
            mh += getSize.Y + pad;
        }
        var msgSize = new Vector2(mw, mh);
        if (_msg.Size != msgSize) _msg.Size = msgSize;
        // Game-over: above the buttons, never over them or the game's buttons where avoidable (PlacePanel).
        // Summary: by the button, off the summary panel (PlaceNear).
        var panel = _spot0 == ShareSpot.GameOver
            ? ShareLayout.PlacePanel(buttons, row, mw, mh, vp.X, vp.Y, sc)
            : ShareLayout.PlaceNear(buttons, _anchor.Union(buttons), mw, mh, vp.X, vp.Y, sc);
        var msgPos = new Vector2(panel.X, panel.Y);
        if (_msg.Position != msgPos) _msg.Position = msgPos;
    }

    // The game-over screen: Share run beside the game's row, Share fight (when set) beside Share run. False while
    // the row isn't frozen yet (nothing shown). `buttons`: the union of the buttons shown, for the panel.
    private bool LayoutGameOver(ulong now, Vector2 vp, float sc, out Box row, out Box buttons)
    {
        row = buttons = default;
        Box summary = default, board = default;
        bool haveScreen = ScreenTransform(out var screen);
        if (haveScreen && !_freeze.Frozen)
        {
            bool complete = RefreshRests();
            Vector2 s = screen.Scale;
            _freeze.Step(complete, screen.Origin.X, screen.Origin.Y, s.X, s.Y, now - _boundAtMs);
        }
        else if (!haveScreen && !_freeze.Frozen && now - _boundAtMs >= RowFreeze.MaxWaitMs)
            _freeze.Step(true, 0f, 0f, 1f, 1f, RowFreeze.MaxWaitMs);   // no screen to read: the scene default
        if (!_freeze.Frozen)
        {
            // Not placed yet: hidden, and the first-appearance glow waits for it.
            _run!.Hide();
            _fight.Hide();
            if (_msg.Visible) _msg.Visible = false;
            return false;
        }
        if (haveScreen)
        {
            // Recomputed only on a new screen, a resize, a UI-scale change or a real move of the screen (its
            // transform excludes the shake), never on a button showing or hiding.
            if (!_rowValid || vp != _rowVp || sc != _rowScale || screen != _rowXf)
            {
                _row = RestRow(screen);
                _rowValid = true; _rowVp = vp; _rowScale = sc; _rowXf = screen;
            }
            summary = Content(_summary, screen);
            board = Content(_board, screen);
        }
        row = _row;
        // The side is kept while it fits (hysteresis), so content arriving on the second page moves it at most
        // once, and only if it would otherwise be covered.
        Box at = ShareLayout.Place(row, summary, board, vp.X, vp.Y, sc, ref _spot);
        Place(_run!, at, now);
        buttons = at;
        if (_fightIndex != null)
        {
            Box fightAt = ShareLayout.PlaceSecond(at, row, summary, board, vp.X, vp.Y, sc);
            Place(_fight, fightAt, now);
            buttons = buttons.Union(fightAt);
        }
        else _fight.Hide();
        return true;
    }

    // Shows `f` at `at`: face, glyph + label centred as a pair, and the first-appearance glow.
    private static void Place(Face f, Box at, ulong now)
    {
        if (!f.Btn.Visible) f.Btn.Visible = true;
        if (f.ShownAtMs == 0) f.ShownAtMs = now;
        var btnPos = new Vector2(at.X, at.Y);
        var btnSize = new Vector2(at.W, at.H);
        if (f.Btn.Position != btnPos) f.Btn.Position = btnPos;
        if (f.Btn.Size != btnSize) f.Btn.Size = btnSize;
        if (f.Label.Size != btnSize) f.Label.Size = btnSize;
        // Glyph + label centred as a pair: the centre-aligned label shifts right by half the glyph and gap.
        var (ix, iy, shift) = ShareLayout.IconRow(btnSize.X, btnSize.Y, f.IconSize, f.IconGap, f.TextW);
        var labelPos = new Vector2(shift, 0f);
        if (f.Label.Position != labelPos) f.Label.Position = labelPos;
        bool iconOn = f.IconSize > 0f;
        if (f.Icon.Visible != iconOn) f.Icon.Visible = iconOn;
        var iconPos = new Vector2(ix, iy);
        var iconSize = new Vector2(f.IconSize, f.IconSize);
        if (f.Icon.Position != iconPos) f.Icon.Position = iconPos;
        if (f.Icon.Size != iconSize) f.Icon.Size = iconSize;

        // The first-appearance glow: a gold halo that pulses, fades, then hides for good on this screen.
        float glow = ShareLayout.Glow(now - f.ShownAtMs);
        bool glowOn = glow > 0.001f;
        if (f.Glow.Visible != glowOn) f.Glow.Visible = glowOn;
        if (glowOn)
        {
            if (f.Glow.Position != btnPos) f.Glow.Position = btnPos;
            if (f.Glow.Size != btnSize) f.Glow.Size = btnSize;
            var m = new Color(1f, 1f, 1f, glow);
            if (f.Glow.Modulate != m) f.Glow.Modulate = m;
        }
    }

    // On a scale change only: the gold face in three states, the halo, and the labels.
    private void StyleButton(float sc)
    {
        try
        {
            float mul = _spot0 == ShareSpot.GameOver ? 1f : SummaryMul;
            int radius = (int)Math.Round(10 * sc * mul);
            int border = Math.Max(1, (int)Math.Round(2 * sc));
            Paint(_sIdle, FaceIdle, Rim, radius, border, sc);
            Paint(_sHover, FaceHover, RimHover, radius, border, sc);
            Paint(_sPressed, FacePressed, Rim, radius, border, sc);
            _sGlow.SetCornerRadiusAll(radius);
            _sGlow.ShadowColor = new Color(UiTheme.BrandGold, 0.85f);
            _sGlow.ShadowSize = Math.Max(1, (int)Math.Round(22 * sc * mul));
            int fs = (int)Math.Round(26 * sc * mul);
            var font = GameAssets.KreonBold ?? UiTheme.DefaultFont;
            if (_run != null) StyleFace(_run, fs, font);
            StyleFace(_fight, fs, font);
        }
        catch (Exception ex) { ErrorLog.Once("share style", ex); }
    }

    private static void StyleFace(Face f, int fs, Font? font)
    {
        UiTheme.Apply(f.Label, fs, Ink, font, Colors.Transparent, 0);
        f.IconSize = (float)Math.Round(ShareLayout.IconSize(fs));
        f.IconGap = (float)Math.Round(fs * 0.35f);
        f.TextW = font != null ? font.GetStringSize(f.Text, HorizontalAlignment.Left, -1f, fs).X : fs * 0.5f * f.Text.Length;
        f.Icon.QueueRedraw();
    }

    // Drawn only when Godot asks (QueueRedraw on a scale change, or a resize): two lines, then three nodes.
    private static void DrawIcon(Control icon)
    {
        try
        {
            var g = ShareLayout.ShareGlyph(icon.Size.X);
            if (g.Radius <= 0f) return;
            var l = new Vector2(g.LX, g.LY);
            var t = new Vector2(g.TX, g.TY);
            var b = new Vector2(g.BX, g.BY);
            icon.DrawLine(l, t, Ink, g.Line, true);
            icon.DrawLine(l, b, Ink, g.Line, true);
            icon.DrawCircle(l, g.Radius, Ink, true, -1f, true);
            icon.DrawCircle(t, g.Radius, Ink, true, -1f, true);
            icon.DrawCircle(b, g.Radius, Ink, true, -1f, true);
        }
        catch (Exception ex) { ErrorLog.Once("share icon", ex); }
    }

    private static void Paint(StyleBoxFlat sb, Color face, Color rim, int radius, int border, float sc)
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

// Where a ShareButton lives: the game-over screen (Share run, plus Share fight for the run's last fight), or
// under the end-of-combat summary (Share fight only).
internal enum ShareSpot { GameOver, Summary }
