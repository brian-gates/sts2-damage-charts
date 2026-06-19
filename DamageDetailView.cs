using System;
using System.Collections.Generic;
using Godot;

namespace STS2_DamageCharts;

// Hotkey-toggled detailed breakdown panel: two columns (Dealt / Taken) of "source — total (xx%)"
// rows for the local player. Centered, semi-transparent, only on screen while toggled.
internal sealed class DamageDetailView
{
    private const float Width = 480f;
    private const float Height = 540f;
    private const float TopMargin = 50f;
    private const int MaxRows = 10;

    private readonly CanvasLayer _layer;
    private readonly Control _container;
    private readonly Control _barLayer;   // behind text
    private readonly List<Label> _labels = new();
    private readonly List<ColorRect> _bars = new();
    private readonly List<TextureRect> _icons = new();
    private readonly Dictionary<string, Texture2D?> _texCache = new();
    private const float IconSize = 18f;

    // Manually-scrolled combat log with a custom, polling-driven scrollbar. (GUI input/wheel doesn't
    // reach controls on our CanvasLayer, so a native ScrollContainer can't work here.)
    private ColorRect _track = null!;
    private ColorRect _grabber = null!;
    private int _logOffset;
    private bool _logAtBottom = true;
    private bool _grabbing;
    private bool _leftLast;
    private float _grabStartY;
    private int _grabStartOffset;
    private float _trackY, _trackH, _grabberY, _grabberH;   // render-state for hit-testing
    private int _maxOffset, _visible;
    private int _lastLogCount = -1;

    // When true, render as an end-of-combat summary (different header, click-through over rewards).
    public bool SummaryMode;

    public DamageDetailView(Node root)
    {
        _layer = new CanvasLayer { Layer = 129, Name = "Sts2DamageChartsDetail", Visible = false };
        root.AddChild(_layer);

        _container = new Control { Name = "Detail", MouseFilter = Control.MouseFilterEnum.Ignore };
        _container.SetSize(new Vector2(Width, Height));
        _layer.AddChild(_container);

        var bg = UiTheme.MakePanel(0.86f, 1f);
        bg.SetPosition(Vector2.Zero);
        bg.SetSize(new Vector2(Width, Height));
        _container.AddChild(bg);

        // Bars go in their own layer added before labels, so text always renders on top.
        _barLayer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _barLayer.SetPosition(Vector2.Zero);
        _barLayer.SetSize(new Vector2(Width, Height));
        _container.AddChild(_barLayer);

        // Custom scrollbar for the log (purely visual; dragged via polling in UpdateMouse).
        _track = new ColorRect { Color = new Color(1f, 1f, 1f, 0.10f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _container.AddChild(_track);
        _grabber = new ColorRect { Color = new Color(UiTheme.Gold.R, UiTheme.Gold.G, UiTheme.Gold.B, 0.65f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _container.AddChild(_grabber);
    }

    // Poll-based scrollbar drag / track paging (called from the mod's tick while interactive).
    public void UpdateMouse(bool leftDown)
    {
        if (!IsValid() || !_layer.Visible || SummaryMode) return;
        try
        {
            Vector2 m = _container.GetLocalMousePosition();
            float tx = Width - 12f;
            bool overTrackX = m.X >= tx - 4f && m.X <= tx + 10f;
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

    public bool IsValid() => GodotObject.IsInstanceValid(_layer);
    public bool Visible => IsValid() && _layer.Visible;
    public void SetVisible(bool v) { if (IsValid()) _layer.Visible = v; }
    public void Dispose() { if (IsValid()) _layer.QueueFree(); }

    public void Render(SourceSnapshot snap, Color[] palette)
    {
        if (!IsValid() || !_layer.Visible) return;
        try
        {
            // Left edge, vertically centered — clear of the centered reward cards.
            try { var vp = _container.GetViewportRect().Size; _container.SetPosition(new Vector2(40f, Math.Max(10f, (vp.Y - Height) * 0.5f))); }
            catch { _container.SetPosition(new Vector2(40f, 60f)); }

            int slot = Math.Min(snap.LocalSlot, snap.PlayerCount - 1);
            if (slot < 0) slot = 0;
            var ps = (slot < snap.PerPlayer.Length) ? snap.PerPlayer[slot] : new PlayerSources();
            var color = palette[Math.Min(slot, palette.Length - 1)];

            int cur = 0, barCur = 0, iconCur = 0;
            string who = (snap.PlayerCount > 1 && slot < snap.Labels.Length) ? $" — {snap.Labels[slot]}" : "";

            var header = Next(ref cur, 16, UiTheme.Gold);
            header.Text = SummaryMode ? $"Combat Summary{who}   (Cmd+D to dismiss)" : $"Damage Breakdown{who}   (Cmd+D to close)";
            header.SetPosition(new Vector2(10f, 8f));

            float colTop = 40f;
            float leftX = 14f, rightX = Width * 0.5f + 6f;
            float colW = Width * 0.5f - 20f;

            RenderColumn(ref cur, ref barCur, ref iconCur, leftX, colTop, colW, "DEALT", ps.Dealt, ps.DealtTotal, color);
            RenderColumn(ref cur, ref barCur, ref iconCur, rightX, colTop, colW, "TAKEN", ps.Taken, ps.TakenTotal, UiTheme.Red);

            RenderLog(ref cur, snap.Log);

            for (int i = cur; i < _labels.Count; i++) _labels[i].Visible = false;
            for (int i = barCur; i < _bars.Count; i++) _bars[i].Visible = false;
            for (int i = iconCur; i < _icons.Count; i++) _icons[i].Visible = false;
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] detail render error: {ex.Message}"); }
    }

    private void RenderColumn(ref int cur, ref int barCur, ref int iconCur, float x, float top, float colW, string title,
                              List<SourceEntry> entries, long total, Color accent)
    {
        var head = Next(ref cur, 13, accent);
        head.Text = $"{title}   {total}";
        head.SetPosition(new Vector2(x, top));

        float y = top + 24f;
        if (entries.Count == 0)
        {
            var none = Next(ref cur, 12, new Color(1f, 1f, 1f, 0.5f));
            none.Text = "—";
            none.SetPosition(new Vector2(x, y));
            return;
        }

        const float rowH = 22f;
        long max = entries[0].Total > 0 ? entries[0].Total : 1; // sorted desc
        int shown = Math.Min(entries.Count, MaxRows);
        for (int i = 0; i < shown; i++)
        {
            var e = entries[i];
            int pct = total > 0 ? (int)Math.Round(100.0 * e.Total / total) : 0;

            // Bar scaled to the column's largest source, drawn behind the text.
            float bw = colW * (float)e.Total / max;
            var bar = NextBar(ref barCur);
            bar.Color = new Color(accent.R, accent.G, accent.B, 0.30f);
            bar.SetPosition(new Vector2(x - 2f, y - 1f));
            bar.SetSize(new Vector2(Math.Max(bw, 2f), rowH - 3f));

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
                    ic.SetSize(new Vector2(IconSize, IconSize));
                    textX = x + IconSize + 4f;
                }
            }

            var row = Next(ref cur, 12, UiTheme.Cream);
            row.Text = $"{Truncate(e.Name, 18)}  {e.Total} ({pct}%)";
            row.SetPosition(new Vector2(textX, y));
            y += rowH;
        }
        if (entries.Count > shown)
        {
            var more = Next(ref cur, 11, new Color(1f, 1f, 1f, 0.5f));
            more.Text = $"+ {entries.Count - shown} more";
            more.SetPosition(new Vector2(x, y));
        }
    }

    private void RenderLog(ref int cur, LogEntry[] log)
    {
        float top = 296f;
        var head = Next(ref cur, 13, UiTheme.Gold);
        head.Text = "Combat Log";
        head.SetPosition(new Vector2(14f, top));

        float listTop = top + 22f, listBottom = Height - 10f, lineH = 16f;
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

        float y = listTop;
        for (int i = _logOffset; i < rows.Count && i < _logOffset + _visible; i++)
        {
            var r = rows[i];
            var lbl = Next(ref cur, r.Sep ? 10 : 11, r.Color);
            lbl.Text = r.Text;
            lbl.SetPosition(new Vector2(r.Sep ? 14f : 18f, y));
            y += lineH;
        }

        // Custom scrollbar on the right edge.
        _trackY = listTop; _trackH = regionH;
        float tx = Width - 12f;
        if (_maxOffset > 0)
        {
            _grabberH = Math.Max(24f, _trackH * _visible / rows.Count);
            _grabberY = _trackY + (_trackH - _grabberH) * ((float)_logOffset / _maxOffset);
            _track.Visible = true; _track.SetPosition(new Vector2(tx, _trackY)); _track.SetSize(new Vector2(6f, _trackH));
            _grabber.Visible = true; _grabber.SetPosition(new Vector2(tx, _grabberY)); _grabber.SetSize(new Vector2(6f, _grabberH));
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

    private Label Next(ref int cursor, int fontSize, Color color)
    {
        Label lbl;
        if (cursor < _labels.Count) lbl = _labels[cursor];
        else { lbl = UiTheme.MakeLabel(fontSize, color); _container.AddChild(lbl); _labels.Add(lbl); }
        UiTheme.Apply(lbl, fontSize, color);
        lbl.Visible = true;
        cursor++;
        return lbl;
    }
}
