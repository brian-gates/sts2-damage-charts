using Godot;

namespace STS2_DamageCharts.Overlay;

// The game's relic row (NRelicInventory, under the top bar inside NGlobalUi). Supplies its bottom edge to
// PanelTop so the always-on panels stay below it, following it when many relics wrap to a second line.
// The node is cached while valid and inside the tree; a miss is retried at most twice a second since the
// lookup walks the tree. Never throws.
internal static class RelicRow
{
    private static Control? _row;
    private static ulong _nextResolveFrame;

    // The top inset for the panels this frame: below the relic row when it is showing, else the fallback.
    public static float TopInset(Node anyNode, Vector2 viewport) => PanelTop.Inset(viewport.Y, Bottom(anyNode));

    // Bottom edge of the relic row in viewport px, or null when it isn't found or isn't visible.
    private static float? Bottom(Node anyNode)
    {
        try
        {
            var row = Resolve(anyNode);
            if (row == null || !row.IsVisibleInTree()) return null;
            Rect2 r = row.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, row.Size);
            return r.End.Y;
        }
        catch { return null; }
    }

    private static Control? Resolve(Node anyNode)
    {
        if (_row != null && GodotObject.IsInstanceValid(_row) && _row.IsInsideTree()) return _row;
        _row = null;
        ulong frame = Engine.GetProcessFrames();
        if (frame < _nextResolveFrame) return null;
        _nextResolveFrame = frame + 30;
        var root = anyNode.GetTree()?.Root;
        if (root == null) return null;
        var globalUi = OverlayMount.FindByType(root, "NGlobalUi");
        _row = (globalUi != null ? OverlayMount.FindByType(globalUi, "NRelicInventory") : null) as Control;
        return _row;
    }
}
