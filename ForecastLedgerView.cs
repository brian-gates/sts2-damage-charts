using System;
using System.Collections.Generic;
using Godot;

namespace STS2_DamageCharts;

// On-demand hover panel for the always-on threat. Top: an "HP lost / round" timeline — one bar per
// recent round (frozen actuals) plus the current round as a live bar (potential → net → range), the
// same visualization the current round settles into when it ends. Below: the current-round source
// breakdown (attacks, DoTs, self-damage cards, hidden note) and the In/Block/Osty/Loss + HP-after
// footer. Follows the cursor, flipping near screen edges. Native nodes; poll-driven from the tick.
internal sealed class ForecastLedgerView
{
    private const float BaseWidth = 268f;
    private const int MaxRows = 10;
    private const int MaxTimeline = 6; // rounds shown (mirrors DamageChartView.MaxGroups)

    private static readonly Color PotentialCol = new(UiTheme.Red.R, UiTheme.Red.G, UiTheme.Red.B, 0.18f);
    private static readonly Color RangeCol = new(UiTheme.Red.R, UiTheme.Red.G, UiTheme.Red.B, 0.42f);

    private readonly CanvasLayer _layer;
    private readonly Control _container;
    private readonly Panel _bg;
    private readonly Control _barLayer;
    private readonly List<ColorRect> _rects = new();
    private readonly List<Label> _labels = new();

    public ForecastLedgerView(Node root)
    {
        _layer = new CanvasLayer { Layer = 132, Name = "Sts2DamageChartsForecastLedger", Visible = false };
        root.AddChild(_layer);
        _container = new Control { Name = "ForecastLedger", MouseFilter = Control.MouseFilterEnum.Ignore };
        _layer.AddChild(_container);
        _bg = UiTheme.MakePanel(0.95f, 1f);
        _bg.SetPosition(Vector2.Zero);
        _container.AddChild(_bg);
        _barLayer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore }; // bars behind text
        _barLayer.SetPosition(Vector2.Zero);
        _container.AddChild(_barLayer);
    }

    public bool IsValid() => GodotObject.IsInstanceValid(_layer);
    public void Hide() { if (IsValid()) _layer.Visible = false; }
    public void Dispose() { if (IsValid()) _layer.QueueFree(); }

    private readonly struct Round
    {
        public readonly int Num, Loss, Potential, MinL, MaxL;
        public readonly bool Live;
        public Round(int num, int loss, int potential, int minL, int maxL, bool live)
        { Num = num; Loss = loss; Potential = potential; MinL = minL; MaxL = maxL; Live = live; }
    }

    public void Render(ForecastResult r, ChartSnapshot history, int localSlot, int currentRound, Vector2 mouseGlobal)
    {
        if (!IsValid()) return;
        try
        {
            _layer.Visible = true;
            Vector2 vp;
            try { vp = _container.GetViewportRect().Size; } catch { vp = new Vector2(1920, 1080); }
            float sc = UiTheme.Scale(vp);
            float W = BaseWidth * sc, rowH = 20f * sc, pad = 8f * sc;
            UiTheme.RestylePanel(_bg, 0.95f, sc);

            int cur = 0, rect = 0;
            float y = pad;

            var head = Next(ref cur, F(15, sc), UiTheme.Gold);
            head.Text = "Incoming Forecast";
            head.SetPosition(new Vector2(pad, y));
            y += 24f * sc;

            // ---- Timeline: HP lost per round (frozen history + live current round) ----
            var rounds = BuildRounds(r, history, localSlot, currentRound);
            if (rounds.Count > 0)
            {
                var tl = Next(ref cur, F(11, sc), new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.55f));
                tl.Text = "HP lost / round";
                tl.SetPosition(new Vector2(pad, y));
                y += 17f * sc;

                int scaleMax = 1;
                foreach (var rd in rounds) scaleMax = Math.Max(scaleMax, Math.Max(rd.Loss, rd.Potential));

                float labelW = 22f * sc, barX = pad + labelW, barW = W - barX - pad;
                float barH = 12f * sc, barRowH = 17f * sc;
                foreach (var rd in rounds)
                {
                    float rowTop = y + (barRowH - barH) * 0.5f;
                    var rl = Next(ref cur, F(11, sc), new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, rd.Live ? 0.95f : 0.6f));
                    rl.Text = rd.Live ? "now" : rd.Num.ToString();
                    rl.SetPosition(new Vector2(pad, y));

                    if (rd.Live)
                    {
                        // potential (faint) → up-to-max (translucent) → guaranteed min (solid)
                        PlaceBar(ref rect, barX, rowTop, barW * rd.Potential / scaleMax, barH, PotentialCol);
                        PlaceBar(ref rect, barX, rowTop, barW * rd.MaxL / scaleMax, barH, RangeCol);
                        PlaceBar(ref rect, barX, rowTop, barW * rd.MinL / scaleMax, barH, UiTheme.Red);
                    }
                    else
                    {
                        PlaceBar(ref rect, barX, rowTop, barW * rd.Loss / scaleMax, barH, new Color(UiTheme.Red.R, UiTheme.Red.G, UiTheme.Red.B, 0.8f));
                    }
                    y += barRowH;
                }
                y += 5f * sc;
                var sepT = Next(ref cur, F(11, sc), new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.4f));
                sepT.Text = "─────────";
                sepT.SetPosition(new Vector2(pad, y));
                y += rowH;
            }

            // ---- Current-round source breakdown ----
            int shown = 0;
            var items = r.Items;
            for (int i = 0; i < items.Count && shown < MaxRows; i++)
            {
                var it = items[i];
                if (it.Kind == ForecastKind.Hidden || it.Total <= 0) continue;
                string text; Color color;
                switch (it.Kind)
                {
                    case ForecastKind.Dot:
                        text = $"{Truncate(it.Label, 18)}  {it.Total}  (unblock.)"; color = UiTheme.Red; break;
                    case ForecastKind.SelfCard:
                        text = $"{Truncate(it.Label, 18)}  +{it.Total}  if played"; color = UiTheme.Gray; break;
                    default:
                        string amt = it.Hits > 1 ? $"{it.Hits}×{it.DamagePerHit}  {it.Total}" : it.Total.ToString();
                        text = $"{Truncate(it.Label, 18)}  {amt}"; color = UiTheme.Cream; break;
                }
                var row = Next(ref cur, F(13, sc), color);
                row.Text = text;
                row.SetPosition(new Vector2(pad + 4f * sc, y));
                y += rowH; shown++;
            }

            if (r.HasHidden)
            {
                var hid = Next(ref cur, F(13, sc), UiTheme.Gray);
                hid.Text = "Hidden intent  ?";
                hid.SetPosition(new Vector2(pad + 4f * sc, y));
                y += rowH;
            }

            var sep = Next(ref cur, F(11, sc), new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.4f));
            sep.Text = "─────────";
            sep.SetPosition(new Vector2(pad, y));
            y += rowH;

            string lossStr = r.MinLoss == r.MaxLoss ? r.MinLoss.ToString() : $"{r.MinLoss}–{r.MaxLoss}";
            string ostyStr = r.OstyAbsorb > 0 ? $"   Osty {r.OstyAbsorb}" : "";
            var foot = Next(ref cur, F(13, sc), r.IsLethal ? UiTheme.Red : UiTheme.Cream);
            foot.Text = $"In {r.Incoming}   Block {r.Block}{ostyStr}   Loss {lossStr}";
            foot.SetPosition(new Vector2(pad, y));
            y += 20f * sc;
            int after = r.CurrentHp - r.MaxLoss; // net-negative when lethal = mitigation still needed
            var hp = Next(ref cur, F(13, sc), r.IsLethal ? UiTheme.Red : UiTheme.Gold);
            hp.Text = $"HP {r.CurrentHp} → {after} after";
            hp.SetPosition(new Vector2(pad, y));
            y += 22f * sc;

            float height = y + pad;
            _container.SetSize(new Vector2(W, height));
            _bg.SetSize(new Vector2(W, height));
            _barLayer.SetSize(new Vector2(W, height));

            float off = 44f * sc;
            float x = mouseGlobal.X + off;
            if (x + W > vp.X) x = mouseGlobal.X - W - off;
            float py = mouseGlobal.Y + off;
            if (py + height > vp.Y) py = mouseGlobal.Y - height - off;
            x = Math.Clamp(x, 0f, Math.Max(0f, vp.X - W));
            py = Math.Clamp(py, 0f, Math.Max(0f, vp.Y - height));
            _container.SetPosition(new Vector2(x, py));

            for (int i = cur; i < _labels.Count; i++) _labels[i].Visible = false;
            for (int i = rect; i < _rects.Count; i++) _rects[i].Visible = false;
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] forecast ledger error: {ex.Message}"); }
    }

    // Recent completed rounds (actual taken) + the live current round (forecast potential/net/range).
    private static List<Round> BuildRounds(ForecastResult r, ChartSnapshot history, int localSlot, int currentRound)
    {
        var completed = new List<Round>();
        try
        {
            if (history != null)
                foreach (var row in history.Rows)
                {
                    if (row.Round >= currentRound) continue; // current round handled live (avoid double-draw)
                    if (localSlot < 0 || localSlot >= row.Taken.Length) continue;
                    completed.Add(new Round(row.Round, row.Taken[localSlot], row.Taken[localSlot], row.Taken[localSlot], row.Taken[localSlot], false));
                }
        }
        catch { }

        var rounds = new List<Round>();
        int take = Math.Max(0, MaxTimeline - 1);
        int start = Math.Max(0, completed.Count - take);
        for (int i = start; i < completed.Count; i++) rounds.Add(completed[i]);
        rounds.Add(new Round(currentRound, r.MinLoss, r.Incoming, r.MinLoss, r.MaxLoss, true));
        return rounds;
    }

    private void PlaceBar(ref int cursor, float x, float y, float w, float h, Color color)
    {
        if (w < 0.5f) return;
        ColorRect rc;
        if (cursor < _rects.Count) rc = _rects[cursor];
        else { rc = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore }; _barLayer.AddChild(rc); _rects.Add(rc); }
        rc.Color = color;
        rc.Visible = true;
        rc.SetPosition(new Vector2(x, y));
        rc.SetSize(new Vector2(w, h));
        cursor++;
    }

    private static int F(int b, float sc) => Math.Max(9, (int)Math.Round(b * sc));
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
