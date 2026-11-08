using System;
using System.Collections.Generic;
using Godot;

namespace STS2_DamageCharts;

// Mounts a view's content so the game's hover tips (relics, potions, powers, cards) draw over it.
//
// The game parents every hover tip under NGame.HoverTipsContainer in the root canvas, so anything on one of
// our high CanvasLayers covers them. Instead the view's content goes in `Holder`, which Place() reparents
// into the tips container's parent immediately before it (later siblings draw on top). Content keeps
// viewport coordinates: the holder cancels the new parent's global canvas transform. When the container
// can't be found the holder stays on the view's own CanvasLayer (identity transform), the old behavior.
//
// Visibility goes through the holder (`Visible`), since toggling the CanvasLayer no longer hides a
// reparented holder. Never throws.
internal sealed class OverlayMount
{
    private readonly Node _root;
    private readonly CanvasLayer _layer;
    private readonly int _rank;
    private readonly ulong _id;
    public Control Holder { get; }

    // Holder instance id -> rank (its view's old layer number) for every live mount, so mounts sharing the
    // spot before the tips keep their old relative order (forecast over chart) without fighting over it.
    private static readonly Dictionary<ulong, int> _ranks = new();
    private static Node? _tips;
    private static ulong _nextResolveFrame;
    private static bool _logged;

    public OverlayMount(Node root, CanvasLayer layer, string name)
    {
        _root = root;
        _layer = layer;
        _rank = layer.Layer;
        Holder = new Control { Name = name, MouseFilter = Control.MouseFilterEnum.Ignore };
        layer.AddChild(Holder);
        _id = Holder.GetInstanceId();
        _ranks[_id] = _rank;
    }

    public bool IsValid => GodotObject.IsInstanceValid(Holder) && GodotObject.IsInstanceValid(_layer);

    public bool Visible
    {
        get { try { return IsValid && Holder.Visible; } catch { return false; } }
        set { try { if (GodotObject.IsInstanceValid(Holder) && Holder.Visible != value) Holder.Visible = value; } catch { } }
    }

    // Call before drawing each frame: keeps the holder just under the hover tips (or on the fallback layer)
    // with a transform that maps its content to viewport coordinates.
    public void Place()
    {
        try
        {
            if (!IsValid) return;
            Node? tips = ResolveHoverTips(_root);
            Node? tipsParent = tips?.GetParent();
            Node target = tipsParent ?? _layer;

            if (Holder.GetParent() != target)
            {
                Holder.GetParent()?.RemoveChild(Holder);
                target.AddChild(Holder);
            }

            if (tips != null && tipsParent != null)
            {
                int to = OverlayTransform.PlaceIndex(Holder.GetIndex(), tips.GetIndex(), _rank, i =>
                {
                    var ch = tipsParent.GetChild(i);
                    return ch != null && _ranks.TryGetValue(ch.GetInstanceId(), out int r) ? r : null;
                });
                if (to >= 0) tipsParent.MoveChild(Holder, to);
            }

            Transform2D g = target is CanvasItem ci ? ci.GetGlobalTransformWithCanvas() : Transform2D.Identity;
            var (px, py, sx, sy) = OverlayTransform.Inverse(g.Scale.X, g.Scale.Y, g.Origin.X, g.Origin.Y);
            var pos = new Vector2(px, py);
            var scale = new Vector2(sx, sy);
            if (Holder.Position != pos) Holder.Position = pos;
            if (Holder.Scale != scale) Holder.Scale = scale;

            if (ForecastReader.Debug && !_logged && tipsParent != null)
            {
                _logged = true;
                GD.Print($"[STS2 Damage] DIAG hover tips parent: {tipsParent.GetPath()} ({tipsParent.GetType().Name}) "
                    + $"global canvas transform={g} -> holder pos={pos} scale={scale}");
            }
        }
        catch (Exception ex) { ErrorLog.Once("overlay mount", ex); }
    }

    // Frees the holder wherever it is mounted (the game would otherwise keep a reparented holder alive),
    // then the layer.
    public void Dispose()
    {
        _ranks.Remove(_id);
        try { if (GodotObject.IsInstanceValid(Holder) && Holder.GetParent() != _layer) Holder.GetParent()?.RemoveChild(Holder); } catch { }
        try { if (GodotObject.IsInstanceValid(Holder)) Holder.QueueFree(); } catch { }
        try { if (GodotObject.IsInstanceValid(_layer)) _layer.QueueFree(); } catch { }
    }

    // NGame.HoverTipsContainer, cached while valid and attached. A miss is retried at most twice a second
    // since finding NGame walks the tree.
    private static Node? ResolveHoverTips(Node root)
    {
        try
        {
            if (_tips != null && GodotObject.IsInstanceValid(_tips) && _tips.GetParent() != null) return _tips;
            _tips = null;
            ulong frame = Engine.GetProcessFrames();
            if (frame < _nextResolveFrame) return null;
            _nextResolveFrame = frame + 30;
            var game = FindByType(root, "NGame");
            var tips = game?.GetType().GetProperty("HoverTipsContainer")?.GetValue(game) as Node;
            if (tips != null && GodotObject.IsInstanceValid(tips) && tips.GetParent() != null) _tips = tips;
        }
        catch { _tips = null; }
        return _tips;
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
}
