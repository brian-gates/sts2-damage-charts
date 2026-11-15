using System;
using Godot;

namespace STS2_DamageCharts;

// The Share button on the victory and death screens (NGameOverScreen), bottom-left, with a small message
// panel above it. Pressing it reads Spire Sync's status file and does what ShareAction says: open
// spiresync://share/<run_id> and copy <server>/runs/<run_id> (a short note), or explain how to get there
// (a panel that stays until closed or Share is pressed again). The mod itself makes no network calls.
//
// Like the other views it sits on its own high CanvasLayer and reads the mouse by polling (press and release
// over the same target is a click), since GUI input doesn't reliably reach controls on these layers. Its
// controls still stop the mouse when GUI input does arrive, so a click doesn't fall through to the screen.
// Never throws.
internal sealed class ShareButton
{
    private const double NoteSeconds = 5.0;

    private readonly CanvasLayer _layer;
    private readonly Control _root;
    private readonly Panel _btn;
    private readonly Label _btnLabel;
    private readonly Panel _msg;
    private readonly Label _msgText;
    private readonly Panel _get;
    private readonly Label _getLabel;
    private readonly Label _close;

    private string _runId = "";
    private string? _message;        // the panel's text; null = no panel
    private bool _showGetApp;
    private bool _closable;
    private ulong _noteUntilMs;      // a transient note hides itself at this time (0 = stays)
    private bool _downLast;
    private Target _armed;           // what the mouse went down on
    private Target _over;            // what the mouse is over, as of the last frame

    private enum Target { None, Share, GetApp, Close }

    public ShareButton(Node root)
    {
        _layer = new CanvasLayer { Layer = 130, Name = "Sts2DamageChartsShare", Visible = false };
        root.AddChild(_layer);
        _root = new Control { Name = "Share", MouseFilter = Control.MouseFilterEnum.Ignore };
        _layer.AddChild(_root);

        _btn = UiTheme.MakePanel(0.9f, 1f);
        _btn.MouseFilter = Control.MouseFilterEnum.Stop;
        _root.AddChild(_btn);
        _btnLabel = UiTheme.MakeLabel(24, UiTheme.Gold);
        _btnLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _btnLabel.VerticalAlignment = VerticalAlignment.Center;
        _btnLabel.Text = "Share";
        _btn.AddChild(_btnLabel);

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

    // A new game-over screen: start without a panel.
    public void Reset(string runId)
    {
        _runId = runId;
        _message = null;
        _armed = Target.None;
        _downLast = true;   // a button still held from the previous screen isn't a press here
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
            if (_noteUntilMs != 0 && Time.GetTicksMsec() >= _noteUntilMs) { _message = null; _noteUntilMs = 0; }
            Layout();

            Vector2 mouse = _root.GetViewport().GetMousePosition();
            Target over = _over = Hit(mouse);
            var lit = new Color(1.25f, 1.25f, 1.25f);
            if (_btn.SelfModulate != (over == Target.Share ? lit : Colors.White)) _btn.SelfModulate = over == Target.Share ? lit : Colors.White;
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
            case Target.Close: _message = null; break;
        }
    }

    private void Share()
    {
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
        Show(action.Message, getApp: false, transient: true);
    }

    private void Show(string text, bool getApp, bool transient)
    {
        _message = text;
        _showGetApp = getApp;
        _closable = !transient;
        _noteUntilMs = transient ? Time.GetTicksMsec() + (ulong)(NoteSeconds * 1000) : 0;
    }

    private void Layout()
    {
        Vector2 vp;
        try { vp = _root.GetViewportRect().Size; } catch { vp = new Vector2(1920, 1080); }
        float sc = UiTheme.Scale(vp);
        float margin = 40f * sc, pad = 12f * sc;

        UiTheme.RestylePanel(_btn, 0.9f, sc);
        UiTheme.Apply(_btnLabel, (int)Math.Round(24 * sc), UiTheme.Gold);
        var btnSize = new Vector2(150f * sc, 54f * sc);
        var btnPos = new Vector2(margin, vp.Y - margin - btnSize.Y);
        if (_btn.Position != btnPos) _btn.Position = btnPos;
        if (_btn.Size != btnSize) _btn.Size = btnSize;
        if (_btnLabel.Size != btnSize) _btnLabel.Size = btnSize;

        bool showMsg = _message != null;
        if (_msg.Visible != showMsg) _msg.Visible = showMsg;
        if (!showMsg) return;

        UiTheme.RestylePanel(_msg, 0.92f, sc);
        UiTheme.RestylePanel(_get, 0.9f, sc);
        int fs = (int)Math.Round(20 * sc);
        UiTheme.Apply(_msgText, fs, UiTheme.Cream);
        UiTheme.Apply(_getLabel, fs, UiTheme.Gold);
        UiTheme.Apply(_close, fs, _over == Target.Close ? UiTheme.Cream : UiTheme.Gray);
        Th.Text(_msgText, _message!);

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
        var msgPos = new Vector2(margin, btnPos.Y - 10f * sc - h);
        if (_msg.Position != msgPos) _msg.Position = msgPos;
    }
}
