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
