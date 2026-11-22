namespace STS2_DamageCharts.Overlay;

internal enum PanelRelease { None, Click, Moved, Resized }

// Pure (unit-tested) press state for a draggable panel, fed viewport-px cursor positions from the poll-based
// mouse. A press anywhere on the panel is a potential move that becomes a move once the cursor travels more
// than MoveThreshold; released before that it is a click. A press on the resize grip resizes and is never a
// click or a move.
internal sealed class PanelDrag
{
    public const float MoveThreshold = 5f;

    private enum Mode { None, Pending, Moving, Resizing }
    private Mode _mode;
    private float _pressX, _pressY;
    private float _offX, _offY;               // press point minus the panel's top-left
    private float _startW, _startH, _startScale;

    public bool IsMoving => _mode == Mode.Moving;
    public bool IsResizing => _mode == Mode.Resizing;
    public bool IsActive => _mode != Mode.None;

    public void Press(float x, float y, bool overPanel, bool overGrip, float panelX, float panelY,
                      float panelW, float panelH, float scale)
    {
        _pressX = x; _pressY = y;
        _offX = x - panelX; _offY = y - panelY;
        _startW = panelW; _startH = panelH; _startScale = scale;
        _mode = overPanel && overGrip ? Mode.Resizing : overPanel ? Mode.Pending : Mode.None;
    }

    public void Move(float x, float y)
    {
        if (_mode == Mode.Pending && Travelled(x, y)) _mode = Mode.Moving;
    }

    public PanelRelease Release(float x, float y)
    {
        var mode = _mode;
        _mode = Mode.None;
        return mode switch
        {
            Mode.Resizing => PanelRelease.Resized,
            Mode.Moving => PanelRelease.Moved,
            Mode.Pending => Travelled(x, y) ? PanelRelease.Moved : PanelRelease.Click,
            _ => PanelRelease.None,
        };
    }

    // Where the panel's top-left goes while moving (before the caller clamps it to the screen).
    public (float X, float Y) DragTopLeft(float x, float y) => (x - _offX, y - _offY);

    // The scale while resizing from the bottom-right corner (top-left fixed): the cursor's travel projected on
    // the panel's diagonal, as a fraction of that diagonal, applied to the scale at the press. Unclamped.
    public float ResizeScale(float x, float y)
    {
        float d2 = _startW * _startW + _startH * _startH;
        if (!(d2 > 0f)) return _startScale;
        float t = ((x - _pressX) * _startW + (y - _pressY) * _startH) / d2;
        return _startScale * (1f + t);
    }

    private bool Travelled(float x, float y)
    {
        float dx = x - _pressX, dy = y - _pressY;
        return dx * dx + dy * dy > MoveThreshold * MoveThreshold;
    }
}
