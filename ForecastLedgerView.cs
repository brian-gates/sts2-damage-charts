using System;
using Godot;

namespace STS2_DamageCharts;

// Hover tooltip for the threat bar: one row per forecast item (attacks, DoTs flagged unblockable,
// self-damage cards flagged if-played), a hidden-intent note, and an Incoming/Block/Loss footer.
// Follows the cursor, flipping near screen edges. Native nodes; poll-driven from the tick.
internal sealed class ForecastLedgerView
{
    private const float BaseWidth = 268f;
    private const int MaxRows = 10;

    private readonly CanvasLayer _layer;
    private readonly Control _container;
    private readonly Panel _bg;
    private readonly System.Collections.Generic.List<Label> _labels = new();

    public ForecastLedgerView(Node root)
    {
        _layer = new CanvasLayer { Layer = 132, Name = "Sts2DamageChartsForecastLedger", Visible = false };
        root.AddChild(_layer);
        _container = new Control { Name = "ForecastLedger", MouseFilter = Control.MouseFilterEnum.Ignore };
        _layer.AddChild(_container);
        _bg = UiTheme.MakePanel(0.95f, 1f);
        _bg.SetPosition(Vector2.Zero);
        _container.AddChild(_bg);
    }

    public bool IsValid() => GodotObject.IsInstanceValid(_layer);
    public void Hide() { if (IsValid()) _layer.Visible = false; }
    public void Dispose() { if (IsValid()) _layer.QueueFree(); }

    public void Render(ForecastResult r, Vector2 mouseGlobal)
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

            int cur = 0;
            float y = pad;

            var head = Next(ref cur, F(15, sc), UiTheme.Gold);
            head.Text = "Incoming Forecast";
            head.SetPosition(new Vector2(pad, y));
            y += 24f * sc;

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
                    default: // Attack / NonAttackHpLoss
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
            y += 20f * sc;
            int after = Math.Max(0, r.CurrentHp - r.MaxLoss);
            var hp = Next(ref cur, F(13, sc), r.IsLethal ? UiTheme.Red : UiTheme.Gold);
            hp.Text = $"HP {r.CurrentHp} → {after} after";
            hp.SetPosition(new Vector2(pad, y));
            foot.SetPosition(new Vector2(pad, y));
            y += 22f * sc;

            float height = y + pad;
            _container.SetSize(new Vector2(W, height));
            _bg.SetSize(new Vector2(W, height));

            float off = 44f * sc;
            float x = mouseGlobal.X + off;
            if (x + W > vp.X) x = mouseGlobal.X - W - off;
            float py = mouseGlobal.Y + off;
            if (py + height > vp.Y) py = mouseGlobal.Y - height - off;
            x = Math.Clamp(x, 0f, Math.Max(0f, vp.X - W));
            py = Math.Clamp(py, 0f, Math.Max(0f, vp.Y - height));
            _container.SetPosition(new Vector2(x, py));

            for (int i = cur; i < _labels.Count; i++) _labels[i].Visible = false;
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] forecast ledger error: {ex.Message}"); }
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
