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
        int hp = 0, maxHp = 0, block = 0, ostyAbsorb = 0;
        bool hasHidden = false;

        try
        {
            Player? mePlayer = (localSlot >= 0 && localSlot < players.Count) ? players[localSlot] : null;
            Creature? me = mePlayer?.Creature;
            if (me != null)
            {
                hp = GetInt(me, "CurrentHp", "Hp", "Health");
                maxHp = GetInt(me, "MaxHp", "MaxHealth");
                block = GetInt(me, "Block", "CurrentBlock");
                ostyAbsorb = ReadOstyAbsorb(mePlayer!);
                if (Debug && !_dumped) { _dumped = true; DumpDiagnostics(cs, players, localSlot, me); }
                ReadDots(me, items);
                ReadSelfDamageCards(cs, players, localSlot, items);
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

        return new ForecastInputs(hp, maxHp, block, ostyAbsorb, hasHidden, items);
    }

    // True while the local player is actively taking their turn (PlayerTurnPhase.Play) — i.e. planning,
    // when the forecast is meaningful. During the enemy turn it reads false, so the caller freezes the
    // last planning value instead of letting it climb as block/Osty are consumed by resolving hits.
    public static bool IsPlanningPhase(IReadOnlyList<Player> players, int localSlot)
    {
        try
        {
            if (localSlot < 0 || localSlot >= players.Count) return false;
            object? pcs = GetProp(players[localSlot], "PlayerCombatState");
            object? phase = pcs != null ? GetProp(pcs, "Phase") : null;
            return phase != null && string.Equals(phase.ToString(), "Play", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // Osty (Necrobinder's summon) is a damage sink in front of the player: its block + HP absorb attacks
    // before overflow reaches the player. Only counts while Osty is alive.
    private static int ReadOstyAbsorb(Player player)
    {
        try
        {
            object? aliveObj = GetProp(player, "IsOstyAlive");
            if (aliveObj is bool alive && !alive) return 0;
            object? osty = GetProp(player, "Osty");
            if (osty == null) return 0;
            int ohp = GetInt(osty, "CurrentHp", "Hp", "Health");
            int oblock = GetInt(osty, "Block", "CurrentBlock");
            if (ohp <= 0 && oblock <= 0) return 0;
            return ohp + oblock;
        }
        catch { return 0; }
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
            string itype = GetProp(intent, "IntentType")?.ToString() ?? "";
            if (!Contains(tn, "Attack") && !Contains(itype, "Attack")) continue; // buff/defend/etc: no HP threat

            int hits = GetInt(intent, "Repeats", "Hits", "Times", "Count", "AttackCount");
            if (hits <= 0) hits = 1;
            int perHit = ResolveIntentDamage(intent, enemy);
            if (perHit <= 0) continue; // attack with no readable number → treat as no threat, not hidden
            items.Add(new ForecastItem(name, ForecastKind.Attack, hits, perHit, blockable: true, conditional: false));
        }
        return true;
    }

    private static readonly System.Text.RegularExpressions.Regex NumRx = new(@"\d+");
    // "take ... damage" / "lose ... HP|health" — self-harm, as opposed to attacks that "deal" damage.
    private static readonly System.Text.RegularExpressions.Regex SelfHarmRx = new(
        @"\b(take|lose)\b.{0,40}\b(damage|hp|health)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
    // The number right after take/lose in resolved card text, e.g. "take 5 damage" -> 5.
    private static readonly System.Text.RegularExpressions.Regex SelfDamageRx = new(
        @"\b(?:take|lose)\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static int ParseSelfDamage(string desc)
    {
        var m = SelfDamageRx.Match(desc);
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : 0;
    }

    // The card's description with {Damage}-style templates resolved to real numbers. GetDescriptionForPile
    // needs the pile context to fill them in; the plain Description property does not.
    private static string? ResolveCardText(object card)
    {
        try
        {
            var m = FindMethod(card, "GetDescriptionForPile", 1);
            if (m == null) return null;
            var pileType = m.GetParameters()[0].ParameterType;
            if (!pileType.IsEnum) return null;
            object hand = Enum.Parse(pileType, "Hand");
            object? res = m.Invoke(card, new[] { hand });
            string? s = res as string ?? (res != null ? TextHelper.SafeGetText(() => res) : null);
            return s != null ? TextHelper.StripRichTextTags(s) : null;
        }
        catch { return null; }
    }

    // The number the player sees. The intent's own display label (GetIntentLabel(targets, self)) already
    // applies the attacker's Strength and the target's Vulnerable — DamageCalc alone is the raw base, so
    // it under-reports once modifiers are in play. Prefer the label; fall back to the base delegate.
    private static int ResolveIntentDamage(object intent, Creature enemy)
    {
        try
        {
            object? cs = GetProp(enemy, "CombatState");
            object? targets = cs != null ? GetProp(cs, "PlayerCreatures") : null;
            if (targets != null)
            {
                var m = FindMethod(intent, "GetIntentLabel", 2);
                object? loc = m?.Invoke(intent, new object[] { targets, enemy });
                string? text = loc != null ? TextHelper.SafeGetText(() => loc) : null;
                if (!string.IsNullOrEmpty(text))
                {
                    var mm = NumRx.Match(text);
                    if (mm.Success && int.TryParse(mm.Value, out int n) && n > 0) return n;
                }
            }
        }
        catch { }

        int d = GetInt(intent, "Damage", "BaseDamage", "Amount");
        if (d > 0) return d;

        object? calc = GetProp(intent, "DamageCalc");
        if (calc is Delegate del)
        {
            object? res = null;
            try { res = del.DynamicInvoke(); } catch { }
            if (res is int ri) return ri;
            if (res is decimal de) return (int)Math.Round(de, MidpointRounding.AwayFromZero);
            if (res is double db) return (int)Math.Round(db, MidpointRounding.AwayFromZero);
            if (res is float fl) return (int)Math.Round(fl, MidpointRounding.AwayFromZero);
            if (res != null)
            {
                int rd = GetInt(res, "Damage", "Total", "TotalDamage", "FinalDamage", "Amount", "Value", "BaseDamage");
                if (rd > 0) return rd;
                if (int.TryParse(res.ToString(), out int p)) return p;
            }
        }
        return 0;
    }

    private static List<object> ResolveIntents(Creature enemy)
    {
        var list = new List<object>();
        // The enemy's telegraphed move lives on the MonsterModel: enemy.Monster.NextMove is a MoveState.
        // Intents hang off the MoveState, or off the actual Move it wraps.
        object? monster = GetProp(enemy, "Monster");
        object? moveState = monster != null ? GetProp(monster, "NextMove") : null;
        if (moveState == null) return list;

        CollectIntents(moveState, list);
        if (list.Count == 0)
        {
            object? inner = GetProp(moveState, "Move");
            if (inner != null) CollectIntents(inner, list);
        }
        return list;
    }

    private static void CollectIntents(object holder, List<object> list)
    {
        object? intents = GetProp(holder, "Intents") ?? Invoke(holder, "GetIntents");
        if (intents is IEnumerable en)
            foreach (var i in en) if (i != null) list.Add(i);
    }

    private static object? Invoke(object obj, string method)
    {
        try
        {
            var m = obj.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            return m?.Invoke(obj, null);
        }
        catch { return null; }
    }

    private static MethodInfo? FindMethod(object obj, string name, int paramCount)
    {
        try
        {
            foreach (var m in obj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name == name && m.GetParameters().Length == paramCount) return m;
        }
        catch { }
        return null;
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

    // Like GetInt but rounds decimal/double values (some game numbers are Decimal, e.g. CalculatedDamage).
    private static int GetNum(object obj, params string[] names)
    {
        foreach (var n in names)
        {
            var v = GetProp(obj, n);
            switch (v)
            {
                case null: continue;
                case int i: return i;
                case decimal de: return (int)Math.Round(de, MidpointRounding.AwayFromZero);
                case double db: return (int)Math.Round(db, MidpointRounding.AwayFromZero);
                case float fl: return (int)Math.Round(fl, MidpointRounding.AwayFromZero);
                default:
                    if (double.TryParse(v.ToString(), out double d) && d != 0) return (int)Math.Round(d, MidpointRounding.AwayFromZero);
                    break;
            }
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

    // Cards in hand that cost the player HP. Two flavors:
    //   • Infection-style cards that bite at end of turn while held → GUARANTEED loss (counts toward min).
    //   • Cards that only cost HP when played → CONDITIONAL (widens the max only).
    // Amount comes from the card's HpLoss field, falling back to a "lose N HP" description scan.
    private static void ReadSelfDamageCards(CombatState cs, IReadOnlyList<Player> players, int localSlot, List<ForecastItem> items)
    {
        try
        {
            if (localSlot < 0 || localSlot >= players.Count) return;
            object? hand = FindHand(players[localSlot], cs);
            if (hand == null) return;
            object? cardsObj = GetProp(hand, "Cards") ?? hand; // hand is a CardPile; its list is under .Cards
            if (cardsObj is not IEnumerable cards) return;
            if (Debug) DebugHand(cards);
            foreach (var card in cards)
            {
                if (card == null) continue;
                string tn = card.GetType().Name;
                // GetDescriptionForPile(Hand) resolves the {Damage} template to the real number; the raw
                // Description LocString leaves it as "{Damage:diff()}".
                string desc = ResolveCardText(card)
                    ?? TextHelper.SafeGetText(() => GetProp(card, "Description") ?? GetProp(card, "Text")) ?? "";

                // Self-harm cards say "take N damage" / "lose N HP" (as opposed to attacks that "deal" it).
                if (!SelfHarmRx.IsMatch(desc)) continue;

                // Amount: the number right after "take"/"lose" in the resolved text; fall back to fields.
                int amount = ParseSelfDamage(desc);
                if (amount <= 0) amount = GetNum(card, "CalculatedDamage", "HpLoss", "Damage", "BaseDamage");
                if (amount <= 0) continue;

                string label = TextHelper.SafeGetText(() => GetProp(card, "Title")) ?? tn;
                // "if this is in your hand" / end-of-turn bite = guaranteed loss; otherwise conditional on play.
                bool held = Contains(desc, "in your hand") || Contains(desc, "end of your turn") || Contains(tn, "Infection");
                if (held)
                    items.Add(new ForecastItem(label, ForecastKind.Dot, 1, amount, blockable: false, conditional: false));
                else
                    items.Add(new ForecastItem(label, ForecastKind.SelfCard, 1, amount, blockable: false, conditional: true));
            }
        }
        catch { }
    }

    private static string _lastHandDbg = "";
    private static void DebugHand(IEnumerable cards)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in cards)
            {
                if (c == null) continue;
                string tn = c.GetType().Name;
                int hp = GetInt(c, "HpLoss");
                int calc = GetNum(c, "CalculatedDamage");
                int dmg = GetInt(c, "Damage", "BaseDamage");
                string? desc = ResolveCardText(c) ?? TextHelper.SafeGetText(() => GetProp(c, "Description"));
                sb.Append($"[{tn} HpLoss={hp} Calc={calc} Damage={dmg} desc=\"{(desc ?? "").Replace("\n", " ")}\"] ");
            }
            string s = sb.ToString();
            if (s != _lastHandDbg) { _lastHandDbg = s; Godot.GD.Print($"[STS2 Damage] DIAG hand: {s}"); }
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

    // The player's hand lives on the combat state, not the Player model. Try the player's combat-state
    // object, the player itself, then the global CombatState — under either common name.
    private static object? FindHand(object player, CombatState cs)
    {
        object? pcs = GetProp(player, "PlayerCombatState") ?? GetProp(player, "CombatState");
        return (pcs != null ? (GetProp(pcs, "Hand") ?? GetProp(pcs, "CardsInHand")) : null)
            ?? GetProp(player, "Hand") ?? GetProp(player, "CardsInHand")
            ?? GetProp(cs, "CardsInHand") ?? GetProp(cs, "Hand");
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
            DumpObj("player creature", me);
            object? mePowers = GetProp(me, "Powers");
            DumpObj("player.Powers", mePowers);
            if (mePowers is IEnumerable powers)
                foreach (var p in powers) { if (p != null) { DumpObj("power", p); break; } }

            foreach (var enemy in cs.Enemies)
            {
                if (enemy == null) continue;
                try { if (!enemy.IsAlive) continue; } catch { }
                DumpObj($"enemy '{SafeName(enemy)}'", enemy);
                object? monster = GetProp(enemy, "Monster");
                object? moveState = monster != null ? GetProp(monster, "NextMove") : null;
                DumpObj("Monster.NextMove (MoveState)", moveState);
                object? innerMove = moveState != null ? GetProp(moveState, "Move") : null;
                DumpObj("MoveState.Move", innerMove);
                object? intents = moveState != null ? (GetProp(moveState, "Intents") ?? Invoke(moveState, "GetIntents")) : null;
                if (intents == null && innerMove != null) intents = GetProp(innerMove, "Intents") ?? Invoke(innerMove, "GetIntents");
                DumpObj("intents", intents);
                if (intents is IEnumerable ien)
                    foreach (var it in ien)
                    {
                        if (it == null) continue;
                        DumpObj("intent", it);
                        if (GetProp(it, "DamageCalc") is Delegate del)
                        {
                            object? res = null; try { res = del.DynamicInvoke(); } catch (Exception e) { Godot.GD.Print($"[STS2 Damage] DIAG DamageCalc threw: {e.Message}"); }
                            if (res is int ri) Godot.GD.Print($"[STS2 Damage] DIAG DamageCalc()=int {ri}");
                            else DumpObj("DamageCalc()", res);
                        }
                    }
                break; // first alive enemy is enough to learn the shape
            }

            if (localSlot >= 0 && localSlot < players.Count)
            {
                object? pl = players[localSlot];
                DumpObj("player object", pl);
                object? pcs = GetProp(pl!, "PlayerCombatState") ?? GetProp(pl!, "CombatState");
                DumpObj("player.PlayerCombatState", pcs);
                Godot.GD.Print($"[STS2 Damage] DIAG IsOstyAlive={GetProp(pl!, "IsOstyAlive")}");
                DumpObj("player.Osty", GetProp(pl!, "Osty"));
                object? hand = FindHand(pl!, cs);
                DumpObj("resolved hand", hand);
                object? cardsObj = hand != null ? (GetProp(hand, "Cards") ?? hand) : null;
                if (cardsObj is IEnumerable cards)
                    foreach (var c in cards) { if (c != null) { DumpObj("hand card", c); break; } }
            }
        }
        catch (Exception ex) { Godot.GD.PrintErr($"[STS2 Damage] DIAG failed: {ex.Message}"); }
    }

    // Generic one-line dump of an object's public instance members (name=value; enumerables as [N items]).
    private static void DumpObj(string label, object? o)
    {
        if (o == null) { Godot.GD.Print($"[STS2 Damage] DIAG {label}=null"); return; }
        var sb = new System.Text.StringBuilder();
        try
        {
            foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object? v = null;
                try { v = p.GetValue(o); } catch { }
                string vs;
                if (v is null) vs = "null";
                else if (v is string s) vs = '"' + (s.Length > 16 ? s[..16] + "…" : s) + '"';
                else if (v is int || v is bool || v is float || v is double || v.GetType().IsEnum) vs = v.ToString() ?? "?";
                else if (v is IEnumerable en) { int c = 0; try { foreach (var _ in en) c++; } catch { } vs = $"[{c} items]"; }
                else vs = "{" + v.GetType().Name + "}";
                sb.Append(p.Name).Append('=').Append(vs).Append("  ");
            }
        }
        catch { }
        Godot.GD.Print($"[STS2 Damage] DIAG {label} ({o.GetType().Name}): {sb}");
    }
}
