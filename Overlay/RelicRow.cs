using System;
using System.Reflection;
using Godot;

namespace STS2_DamageCharts.Overlay;

// The game's relic row (NRelicInventory, under the top bar inside NGlobalUi). Supplies its bottom edge to
// PanelTop so the always-on panels stay below it, following it when many relics wrap to a second line.
// NRelicInventory is an HFlowContainer whose own rect is far taller than the relics it shows, so its Size is
// never used. The bottom comes from, in order:
//   1. the game's own NRelicInventory.GetBottomOfInventory() (undocumented; listed in sts2.xml's Godot
//      MethodName cache). It returns a Vector2 in the row's PARENT's local space (the row's position captured
//      at _Ready, moved down by its wrapped lines), so it is mapped to viewport px through the parent's
//      canvas transform. Being based on the _Ready position, it doesn't follow the row's hide/show tweens,
//      so panels don't jump when the row slides away. Called through a bound delegate (no boxing per call);
//      a float or double return (a future game change) is read as the Y through reflection;
//   2. else the union of the row's visible child Controls (the relic holders).
// PanelTop ignores a bottom deeper than 40% of the viewport. The node is cached while valid and inside the
// tree; a miss is retried at most twice a second since the lookup walks the tree. Never throws.
internal static class RelicRow
{
    private static Control? _row;
    private static ulong _nextResolveFrame;
    private static MethodInfo? _bottomMethod;
    private static bool _bottomResolved;
    private static bool _logged;

    // The top inset for the panels this frame: below the relic row when it is showing, else the fallback.
    public static float TopInset(Node anyNode, Vector2 viewport) => PanelTop.Inset(viewport.Y, Bottom(anyNode, viewport.Y));

    // Bottom edge of the relic row in viewport px, or null when it isn't found or isn't visible.
    // A game value PanelTop would reject (deeper than its 40% guard) falls through to the relic holders.
    private static float? Bottom(Node anyNode, float viewportHeight)
    {
        try
        {
            var row = Resolve(anyNode);
            if (row == null || !row.IsVisibleInTree()) return null;
            float? game = GameBottom(row);
            bool useGame = game is float g && g <= viewportHeight * PanelTop.MaxRelicBottomFrac;
            float? b = useGame ? game : ChildrenBottom(row);
            if (!_logged && b != null)
            {
                _logged = true;
                GD.Print($"[STS2 Damage] relic row bottom {b.Value:0} px of {viewportHeight:0} " +
                         $"({(useGame ? "GetBottomOfInventory" : "relic holders")}; game value {(game?.ToString("0") ?? "none")})");
            }
            return b;
        }
        catch { return null; }
    }

    private static Func<Vector2>? _bottomCall;   // GetBottomOfInventory bound to _bottomTarget
    private static Control? _bottomTarget;

    private static float? GameBottom(Control row)
    {
        try
        {
            if (!_bottomResolved)
            {
                _bottomResolved = true;
                _bottomMethod = row.GetType().GetMethod("GetBottomOfInventory",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            }
            if (_bottomMethod == null) return null;
            Vector2 local;
            if (_bottomMethod.ReturnType == typeof(Vector2))
            {
                if (_bottomCall == null || !ReferenceEquals(_bottomTarget, row))
                {
                    _bottomCall = (Func<Vector2>)Delegate.CreateDelegate(typeof(Func<Vector2>), row, _bottomMethod);
                    _bottomTarget = row;
                }
                local = _bottomCall();
            }
            else
            {
                switch (_bottomMethod.Invoke(row, null))
                {
                    case float f: local = new Vector2(0f, f); break;
                    case double d: local = new Vector2(0f, (float)d); break;
                    default: return null;
                }
            }
            if (!float.IsFinite(local.Y)) return null;
            // Parent-local -> viewport px (the parent's canvas transform; the row's own would add its position twice).
            var xf = row.GetParent() is CanvasItem parent ? parent.GetGlobalTransformWithCanvas() : row.GetCanvasTransform();
            return (xf * local).Y;
        }
        catch { return null; }
    }

    // Union of the visible relic holders' rects, in viewport px; null without any.
    private static float? ChildrenBottom(Control row)
    {
        float? bottom = null;
        foreach (var child in row.GetChildren())
        {
            if (child is not Control c || !c.IsVisibleInTree() || c.Size.Y <= 0f) continue;
            Rect2 r = c.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, c.Size);
            bottom = bottom is float b ? Math.Max(b, r.End.Y) : r.End.Y;
        }
        return bottom;
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
