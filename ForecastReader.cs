using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace STS2_DamageCharts;

// Reads live combat state into a plain ForecastInputs. Every game access is reflection-by-name so a
// game rename degrades to "unknown" (hidden flag) rather than crashing combat. Never throws.
internal static class ForecastReader
{
    // Opt-in (config "forecast_debug": true). Dumps real type/member names once per session so the
    // reflection candidate lists can be confirmed/corrected against the live game. No-op when false.
    public static bool Debug;
    private static bool _dumped;

    public static ForecastInputs Build(CombatState cs, IReadOnlyList<Player> players, int localSlot)
    {
        var items = new List<ForecastItem>();
        int hp = 0, maxHp = 0, block = 0;
        bool hasHidden = false;

        try
        {
            Creature? me = (localSlot >= 0 && localSlot < players.Count) ? players[localSlot].Creature : null;
            if (me != null)
            {
                hp = GetInt(me, "CurrentHp", "Hp", "Health");
                maxHp = GetInt(me, "MaxHp", "MaxHealth");
                block = GetInt(me, "Block", "CurrentBlock");
                if (Debug && !_dumped) { _dumped = true; DumpDiagnostics(cs, players, localSlot, me); }
                ReadDots(me, items);
                ReadSelfDamageCards(players, localSlot, items);
            }
        }
        catch { }

        try
        {
            foreach (var enemy in cs.Enemies)
            {
                if (enemy == null) continue;
                try { if (!enemy.IsAlive) continue; } catch { }
                if (!TryReadEnemy(enemy, items)) hasHidden = true;
            }
        }
        catch { }

        return new ForecastInputs(hp, maxHp, block, hasHidden, items);
    }

    // Returns true if this enemy's intent was concretely readable (attack or a known non-attack move);
    // false when the intent is hidden/unreadable, so the caller raises the hidden flag.
    private static bool TryReadEnemy(Creature enemy, List<ForecastItem> items)
    {
        var intents = ResolveIntents(enemy);
        if (intents.Count == 0) return false;

        string name = SafeName(enemy);
        foreach (var intent in intents)
        {
            string tn = intent.GetType().Name;
            if (Contains(tn, "Hidden") || Contains(tn, "Unknown")) return false;
            if (!Contains(tn, "Attack")) continue; // buff/defend/etc: known, no HP threat

            int perHit = GetInt(intent, "Damage", "BaseDamage", "Amount");
            int hits = GetInt(intent, "Hits", "Times", "Count", "AttackCount");
            if (hits <= 0) hits = 1;
            if (perHit <= 0) continue; // attack with no readable number → treat as no threat, not hidden
            items.Add(new ForecastItem(name, ForecastKind.Attack, hits, perHit, blockable: true, conditional: false));
        }
        return true;
    }

    private static List<object> ResolveIntents(Creature enemy)
    {
        var list = new List<object>();
        object? single = GetProp(enemy, "NextMove") ?? GetProp(enemy, "Intent") ?? GetProp(enemy, "CurrentMove");
        if (single != null) { list.Add(single); return list; }
        if (GetProp(enemy, "Intents") is IEnumerable en)
            foreach (var i in en) if (i != null) list.Add(i);
        return list;
    }

    private static string SafeName(Creature enemy)
    {
        try { return TextHelper.SafeGetText(() => enemy.Monster!.Title) ?? "Enemy"; }
        catch { return "Enemy"; }
    }

    private static bool Contains(string s, string sub) =>
        s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;

    private static object? GetProp(object obj, string name)
    {
        try
        {
            var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p.GetValue(obj);
            var f = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return f?.GetValue(obj);
        }
        catch { return null; }
    }

    private static int GetInt(object obj, params string[] names)
    {
        foreach (var n in names)
        {
            var v = GetProp(obj, n);
            if (v is int i) return i;
            if (v != null && int.TryParse(v.ToString(), out int p)) return p;
        }
        return 0;
    }

    private static readonly string[] DotKeywords = { "Bleed", "Burn", "Poison", "Regret", "Bomb", "Constrict" };

    // Player powers that tick HP loss this turn regardless of action. Damage magnitude is the power's
    // stack/amount for stacking DoTs (Bleed/Poison); best-effort for others. Bypass block.
    private static void ReadDots(Creature me, List<ForecastItem> items)
    {
        try
        {
            if (GetProp(me, "Powers") is not IEnumerable powers) return;
            foreach (var p in powers)
            {
                if (p == null) continue;
                string tn = p.GetType().Name;
                string id = GetString(p, "Id", "PowerId") ?? tn;
                if (!MatchesAny(tn, DotKeywords) && !MatchesAny(id, DotKeywords)) continue;
                int amount = GetInt(p, "Amount", "Stacks", "Count", "Value");
                if (amount <= 0) continue;
                string label = TextHelper.SafeGetText(() => GetProp(p, "Title")) ?? Prettify(tn);
                items.Add(new ForecastItem(label, ForecastKind.Dot, 1, amount, blockable: false, conditional: false));
            }
        }
        catch { }
    }

    // Cards in hand whose description says they cost the player HP. Conditional — only counts if played,
    // so it widens the range MAX. Description-scan heuristic (no hard-coded card list). Bypass block.
    private static void ReadSelfDamageCards(IReadOnlyList<Player> players, int localSlot, List<ForecastItem> items)
    {
        try
        {
            if (localSlot < 0 || localSlot >= players.Count) return;
            object? hand = GetProp(players[localSlot], "Hand") ?? GetProp(players[localSlot], "CardsInHand");
            if (hand is not IEnumerable cards) return;
            foreach (var card in cards)
            {
                if (card == null) continue;
                string? desc = TextHelper.SafeGetText(() =>
                    GetProp(card, "Description") ?? GetProp(card, "Text") ?? GetProp(card, "Body"));
                if (desc == null) continue;
                int hpCost = ParseHpLoss(desc);
                if (hpCost <= 0) continue;
                string label = TextHelper.SafeGetText(() => GetProp(card, "Title")) ?? "Card";
                items.Add(new ForecastItem(label, ForecastKind.SelfCard, 1, hpCost, blockable: false, conditional: true));
            }
        }
        catch { }
    }

    // Matches "lose N HP" / "lose N Health" / "N HP loss" case-insensitively; returns the largest number found.
    private static int ParseHpLoss(string desc)
    {
        try
        {
            var rx = new System.Text.RegularExpressions.Regex(
                @"lose\s+(\d+)\s*(?:hp|health)|(\d+)\s*(?:hp|health)\s*loss",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            int max = 0;
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(desc))
            {
                string g = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (int.TryParse(g, out int n) && n > max) max = n;
            }
            return max;
        }
        catch { return 0; }
    }

    private static bool MatchesAny(string s, string[] subs)
    {
        foreach (var k in subs) if (Contains(s, k)) return true;
        return false;
    }

    private static string? GetString(object obj, params string[] names)
    {
        foreach (var n in names) { var v = GetProp(obj, n); if (v is string s && s.Length > 0) return s; }
        return null;
    }

    private static string Prettify(string typeName)
    {
        string n = typeName;
        foreach (var suffix in new[] { "PowerModel", "Power", "Model" })
            if (n.EndsWith(suffix)) { n = n[..^suffix.Length]; break; }
        return n.Length > 0 ? n : typeName;
    }

    private static void DumpDiagnostics(CombatState cs, IReadOnlyList<Player> players, int localSlot, Creature me)
    {
        try
        {
            Godot.GD.Print($"[STS2 Damage] DIAG player creature type={me.GetType().FullName}");
            Godot.GD.Print($"[STS2 Damage] DIAG player ints: {IntMembers(me)}");
            if (GetProp(me, "Powers") is IEnumerable powers)
                foreach (var p in powers) { if (p == null) continue; Godot.GD.Print($"[STS2 Damage] DIAG power type={p.GetType().Name} ints={IntMembers(p)}"); }
            else Godot.GD.Print("[STS2 Damage] DIAG player has no readable 'Powers'");

            foreach (var enemy in cs.Enemies)
            {
                if (enemy == null) continue;
                try { if (!enemy.IsAlive) continue; } catch { }
                var intents = ResolveIntents(enemy);
                Godot.GD.Print($"[STS2 Damage] DIAG enemy='{SafeName(enemy)}' intents={intents.Count}");
                foreach (var it in intents)
                    Godot.GD.Print($"[STS2 Damage] DIAG   intent type={it.GetType().Name} ints={IntMembers(it)}");
            }

            if (localSlot >= 0 && localSlot < players.Count)
            {
                object? hand = GetProp(players[localSlot], "Hand") ?? GetProp(players[localSlot], "CardsInHand");
                if (hand is IEnumerable cards)
                    foreach (var c in cards) { if (c == null) continue; Godot.GD.Print($"[STS2 Damage] DIAG hand card type={c.GetType().Name} strs={StringMembers(c)}"); break; }
                else Godot.GD.Print("[STS2 Damage] DIAG player has no readable 'Hand'/'CardsInHand'");
            }
        }
        catch (Exception ex) { Godot.GD.PrintErr($"[STS2 Damage] DIAG failed: {ex.Message}"); }
    }

    private static string IntMembers(object obj)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (p.PropertyType != typeof(int)) continue;
                try { sb.Append(p.Name).Append('=').Append(p.GetValue(obj)).Append(' '); } catch { }
            }
        }
        catch { }
        return sb.ToString();
    }

    private static string StringMembers(object obj)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (p.PropertyType != typeof(string)) continue;
                sb.Append(p.Name).Append(' ');
            }
        }
        catch { }
        return sb.ToString();
    }
}
