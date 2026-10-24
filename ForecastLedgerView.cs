using System;
using System.Collections;
using System.Collections.Generic;
using Godot;
using HarmonyLib;

namespace STS2_DamageCharts;

// On-demand hover panel for the always-on threat. Top: an "HP lost / round" timeline — one bar per
// recent round (frozen actuals) plus the current round as a live bar (potential → net → range), the
// same visualization the current round settles into when it ends. Below: the current-round source
// breakdown (attacks, DoTs, self-damage cards, hidden note) and the In/Block/Osty/Loss + HP-after
// footer. Follows the cursor, flipping near screen edges. Native nodes; poll-driven from the tick.
// With native_visuals on it reads as a game hover tip: the hover-tip frame and shadow, the tip's
// title/body Kreon with its soft shadow instead of an outline, numeric Kreon for the footer numbers,
// and a leading icon per row (the enemy's attack intent, the DoT's power, the self-damage card).
internal sealed class ForecastLedgerView
{
    private const float BaseWidth = 268f;
    private const int MaxRows = 10;
    private const int MaxTimeline = 6; // rounds shown (mirrors DamageChartView.MaxGroups)

    private static readonly Color PotentialCol = new(UiTheme.Red.R, UiTheme.Red.G, UiTheme.Red.B, 0.18f);
    private static readonly Color RangeCol = new(UiTheme.Red.R, UiTheme.Red.G, UiTheme.Red.B, 0.42f);

    private readonly CanvasLayer _layer;
    private readonly Control _container;
    private readonly Panel _shadow;   // hover-tip drop shadow; only shown with the native panel
    private readonly Panel _bg;
    private readonly Control _barLayer;
    private readonly List<ColorRect> _rects = new();
    private readonly List<Label> _labels = new();
    private readonly List<TextureRect> _icons = new();
    private bool _nativePanel;

    public ForecastLedgerView(Node root)
    {
        _layer = new CanvasLayer { Layer = 132, Name = "Sts2DamageChartsForecastLedger", Visible = false };
        root.AddChild(_layer);
        _container = new Control { Name = "ForecastLedger", MouseFilter = Control.MouseFilterEnum.Ignore };
        _layer.AddChild(_container);
        _shadow = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _container.AddChild(_shadow);
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
            if (!_nativePanel) _nativePanel = ForecastStyle.TryNativePanel(_bg, _shadow);
            if (!_nativePanel) UiTheme.RestylePanel(_bg, 0.95f, sc);
            // The hover-tip frame is ~8px thick; keep text clear of it.
            float W = BaseWidth * sc, rowH = 20f * sc, pad = (_nativePanel ? 13f : 8f) * sc;
            _sc = sc;
            _titleFont = GameAssets.NumericFont;  // hover_tip Title: kreon_bold_glyph_space_one
            _bodyFont = GameAssets.BodyFont;      // hover_tip Description: kreon_regular_glyph_space_one

            int cur = 0, rect = 0, icon = 0;
            float y = pad;

            var head = Next(ref cur, F(15, sc), _titleFont != null ? ForecastStyle.TitleGold : UiTheme.Gold, _titleFont);
            head.Text = "Incoming Forecast";
            head.SetPosition(new Vector2(pad, y));
            y += 24f * sc;

            // ---- Timeline: HP lost per round (frozen history + live current round) ----
            var rounds = BuildRounds(r, history, localSlot, currentRound);
            if (rounds.Count > 0)
            {
                var tl = Next(ref cur, F(11, sc), new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.55f), _bodyFont);
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
                    var rl = Next(ref cur, F(11, sc), new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, rd.Live ? 0.95f : 0.6f), _bodyFont);
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
                var sepT = Next(ref cur, F(11, sc), new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.4f), _bodyFont);
                sepT.Text = "─────────";
                sepT.SetPosition(new Vector2(pad, y));
                y += rowH;
            }

            // ---- Current-round source breakdown ----
            // Native: a leading icon column (reserved for every row once any row has an icon, so text aligns).
            float iconSz = 18f * sc;
            var rowIcons = new List<Texture2D?>();
            bool anyIcon = false;
            {
                int n = 0;
                for (int i = 0; i < r.Items.Count && n < MaxRows; i++)
                {
                    var it = r.Items[i];
                    if (it.Kind == ForecastKind.Hidden || it.Total <= 0) continue;
                    var tex = ForecastIcons.For(it);
                    anyIcon |= tex != null;
                    rowIcons.Add(tex);
                    n++;
                }
            }
            Texture2D? hiddenIcon = r.HasHidden ? GameAssets.Texture(ForecastStyle.UnknownIntentTex) : null;
            anyIcon |= hiddenIcon != null;
            float textX = pad + 4f * sc + (anyIcon ? iconSz + 5f * sc : 0f);

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
                var row = Next(ref cur, F(13, sc), color, _bodyFont);
                row.Text = text;
                row.SetPosition(new Vector2(textX, y));
                PlaceIcon(ref icon, rowIcons[shown], pad + 4f * sc, y + (rowH - iconSz) * 0.5f, iconSz);
                y += rowH; shown++;
            }

            if (r.HasHidden)
            {
                var hid = Next(ref cur, F(13, sc), UiTheme.Gray, _bodyFont);
                hid.Text = "Hidden intent  ?";
                hid.SetPosition(new Vector2(textX, y));
                PlaceIcon(ref icon, hiddenIcon, pad + 4f * sc, y + (rowH - iconSz) * 0.5f, iconSz);
                y += rowH;
            }

            var sep = Next(ref cur, F(11, sc), new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.4f), _bodyFont);
            sep.Text = "─────────";
            sep.SetPosition(new Vector2(pad, y));
            y += rowH;

            string lossStr = r.MinLoss == r.MaxLoss ? r.MinLoss.ToString() : $"{r.MinLoss}–{r.MaxLoss}";
            string ostyStr = r.OstyAbsorb > 0 ? $"   Osty {r.OstyAbsorb}" : "";
            var foot = Next(ref cur, F(13, sc), r.IsLethal ? UiTheme.Red : UiTheme.Cream, GameAssets.NumericFont);
            foot.Text = $"In {r.Incoming}   Block {r.Block}{ostyStr}   Loss {lossStr}";
            foot.SetPosition(new Vector2(pad, y));
            y += 20f * sc;
            int after = r.CurrentHp - r.MaxLoss; // net-negative when lethal = mitigation still needed
            var hp = Next(ref cur, F(13, sc), r.IsLethal ? UiTheme.Red : UiTheme.Gold, GameAssets.NumericFont);
            hp.Text = $"HP {r.CurrentHp} → {after} after";
            hp.SetPosition(new Vector2(pad, y));
            y += 22f * sc;

            float height = y + pad;
            _container.SetSize(new Vector2(W, height));
            _bg.SetSize(new Vector2(W, height));
            _shadow.SetSize(new Vector2(W, height));
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
            for (int i = icon; i < _icons.Count; i++) _icons[i].Visible = false;
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

    private float _sc = 1f;
    private Font? _titleFont, _bodyFont;

    // font: a game font to use instead of UiTheme's (null keeps the current look). With one, the label
    // takes the hover tip's text treatment: no outline, soft 25% black shadow.
    private Label Next(ref int cursor, int fontSize, Color color, Font? font = null)
    {
        Label lbl;
        if (cursor < _labels.Count) lbl = _labels[cursor];
        else { lbl = UiTheme.MakeLabel(fontSize, color); _container.AddChild(lbl); _labels.Add(lbl); }
        UiTheme.Apply(lbl, fontSize, color);
        if (font != null)
        {
            ForecastStyle.SetFont(lbl, font);
            ForecastStyle.Outline(lbl, UiTheme.Outline, _nativePanel ? 0 : 4);
            ForecastStyle.Shadow(lbl, _sc);
        }
        else lbl.RemoveThemeColorOverride("font_shadow_color"); // pooled label may carry a native shadow
        lbl.Visible = true;
        cursor++;
        return lbl;
    }

    private void PlaceIcon(ref int cursor, Texture2D? tex, float x, float y, float size)
    {
        if (tex == null) return;
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
        ic.Texture = tex;
        ic.Visible = true;
        ic.SetPosition(new Vector2(x, y));
        ic.SetSize(new Vector2(size, size));
        cursor++;
    }
}


// Leading icons for ledger rows, all from the game's own art (null when native_visuals is off or nothing
// matches, and the row simply has no icon):
//   Attack   -> intent_attack_N, N from the game's AttackIntent thresholds on the row's TOTAL damage.
//   DoT      -> the power's icon (PowerModel.PackedIconPath); held cards like Infection fall back to cards.
//   SelfCard -> the card's portrait (CardModel.PortraitPath, else PortraitPngPath).
// ForecastItem only carries the localized title, so titles are matched against the canonical models in
// ModelDb.AllPowers / ModelDb.AllCards (reflection by name), built once and cached per label.
internal static class ForecastIcons
{
    private const ulong RetryMs = 5000;
    private static Dictionary<string, object>? _powers, _cards;
    private static ulong _lastBuildTry;
    private static bool _buildTried;
    private static readonly Dictionary<string, Texture2D?> _byLabel = new();

    public static Texture2D? For(ForecastItem it)
    {
        if (!GameAssets.Enabled) return null;
        try
        {
            switch (it.Kind)
            {
                case ForecastKind.Attack: return GameAssets.IntentAttackIcon(it.Total);
                case ForecastKind.Hidden: return GameAssets.Texture(ForecastStyle.UnknownIntentTex);
                case ForecastKind.SelfCard: return ByLabel("card:", it.Label, powerFirst: false);
                default: return ByLabel("dot:", it.Label, powerFirst: true);
            }
        }
        catch { return null; }
    }

    private static Texture2D? ByLabel(string prefix, string label, bool powerFirst)
    {
        string key = prefix + Norm(label);
        if (_byLabel.TryGetValue(key, out var hit)) return hit;
        if (!EnsureMaps()) return null; // ModelDb not ready yet: don't cache the miss

        string n = Norm(label);
        Texture2D? tex = null;
        if (powerFirst) tex = PowerTex(n) ?? CardTex(n);
        else tex = CardTex(n);
        _byLabel[key] = tex;
        return tex;
    }

    private static Texture2D? PowerTex(string n)
    {
        if (_powers != null && _powers.TryGetValue(n, out var model)) { var t = GameAssets.PowerIcon(model); if (t != null) return t; }
        // English-title guess at the entry ("Poison" -> poison_power), for a model the title map missed.
        return GameAssets.PowerIcon(n.Replace(' ', '_') + "_power");
    }

    private static Texture2D? CardTex(string n)
    {
        if (_cards == null || !_cards.TryGetValue(n, out var card)) return null;
        foreach (var prop in new[] { "PortraitPath", "PortraitPngPath" })
        {
            try
            {
                if (card.GetType().GetProperty(prop)?.GetValue(card) is string p && p.Length > 0)
                {
                    var t = GameAssets.Texture(p);
                    if (t != null) return t;
                }
            }
            catch { }
        }
        return null;
    }

    // Title -> canonical model maps from MegaCrit.Sts2.Core.Models.ModelDb's static AllPowers / AllCards.
    private static bool EnsureMaps()
    {
        if (_powers != null && _cards != null) return true;
        ulong now = Time.GetTicksMsec();
        if (_buildTried && now - _lastBuildTry < RetryMs) return false;
        _buildTried = true; _lastBuildTry = now;
        try
        {
            var db = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.ModelDb");
            if (db == null) return false;
            var powers = TitleMap(db, "AllPowers");
            var cards = TitleMap(db, "AllCards");
            if (powers.Count == 0 && cards.Count == 0) return false;
            _powers = powers; _cards = cards;
            return true;
        }
        catch { return false; }
    }

    private static Dictionary<string, object> TitleMap(Type db, string listProp)
    {
        var map = new Dictionary<string, object>();
        try
        {
            if (AccessTools.Property(db, listProp)?.GetValue(null) is not IEnumerable all) return map;
            foreach (var m in all)
            {
                if (m == null) continue;
                // PowerModel.Title is a LocString, CardModel.Title a string; SafeGetText takes both.
                string? t = TextHelper.SafeGetText(() => m.GetType().GetProperty("Title")?.GetValue(m));
                if (!string.IsNullOrEmpty(t)) map.TryAdd(Norm(t), m);
            }
        }
        catch { }
        return map;
    }

    // Case/space-insensitive, and drops an upgrade suffix ("Bloodletting+" / "Searing Blow+3").
    private static string Norm(string s)
    {
        s = (s ?? "").Trim();
        int plus = s.LastIndexOf('+');
        if (plus > 0)
        {
            bool digits = true;
            for (int i = plus + 1; i < s.Length; i++) if (!char.IsDigit(s[i])) { digits = false; break; }
            if (digits) s = s[..plus].TrimEnd();
        }
        return s.ToLowerInvariant();
    }
}
