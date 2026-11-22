using Xunit;
using STS2_DamageCharts.Overlay;

public class PanelDragTests
{
    private static PanelDrag PressAt(float x, float y, bool overPanel = true, bool overGrip = false)
    {
        var d = new PanelDrag();
        d.Press(x, y, overPanel, overGrip, panelX: 100f, panelY: 200f, panelW: 200f, panelH: 100f, scale: 1f);
        return d;
    }

    [Fact]
    public void PressAnywhere_ThenMoveBeyondThreshold_Drags()
    {
        var d = PressAt(250f, 280f);              // the body, well below any header strip
        Assert.False(d.IsMoving);
        d.Move(253f, 283f);                       // ~4.2 px: still a potential click
        Assert.False(d.IsMoving);
        d.Move(260f, 280f);                       // 10 px: a drag
        Assert.True(d.IsMoving);
        var (x, y) = d.DragTopLeft(260f, 280f);   // the panel follows the cursor from where it was grabbed
        Assert.Equal((110f, 200f), (x, y));
        Assert.Equal(PanelRelease.Moved, d.Release(260f, 280f));
        Assert.False(d.IsActive);
    }

    [Fact]
    public void PressAndReleaseWithinThreshold_Clicks()
    {
        var d = PressAt(250f, 280f);
        d.Move(252f, 283f);
        Assert.Equal(PanelRelease.Click, d.Release(253f, 284f));
        Assert.False(d.IsMoving);
    }

    [Fact]
    public void ReleaseFarAwayWithoutMoveEvents_IsADragNotAClick()
    {
        var d = PressAt(250f, 280f);
        Assert.Equal(PanelRelease.Moved, d.Release(270f, 280f));
    }

    [Fact]
    public void PressOffPanel_DoesNothing()
    {
        var d = PressAt(10f, 10f, overPanel: false);
        d.Move(100f, 100f);
        Assert.False(d.IsActive);
        Assert.Equal(PanelRelease.None, d.Release(100f, 100f));
    }

    [Fact]
    public void PressOnGrip_ResizesAndNeverClicks()
    {
        var d = PressAt(295f, 295f, overGrip: true);
        Assert.True(d.IsResizing);
        Assert.False(d.IsMoving);
        Assert.Equal(PanelRelease.Resized, d.Release(295f, 295f));   // no movement still isn't a click

        d = PressAt(295f, 295f, overGrip: true);
        d.Move(400f, 400f);
        Assert.False(d.IsMoving);                                    // a grip press never moves the panel
        Assert.Equal(PanelRelease.Resized, d.Release(400f, 400f));
    }

    [Fact]
    public void ResizeScale_FollowsTheDiagonal()
    {
        var d = PressAt(300f, 300f, overGrip: true);   // bottom-right corner of the 200x100 panel
        Assert.Equal(1f, d.ResizeScale(300f, 300f), 3);
        Assert.Equal(1.5f, d.ResizeScale(400f, 350f), 3);  // corner dragged to 1.5x the size
        Assert.Equal(0.5f, d.ResizeScale(200f, 250f), 3);  // unclamped here; ChartScale clamps
    }
}
