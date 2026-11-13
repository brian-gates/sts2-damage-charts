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
        int hp = 0, maxHp = 0, block = 0, ostyAbsorb = 0, ostyHp = 0, endTurnBlock = 0, buffer = 0;
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
                ostyAbsorb = ReadOstyAbsorb(mePlayer!, out ostyHp);
                endTurnBlock = ReadEndTurnBlock(cs, mePlayer!, me, block);
                buffer = ReadPowerAmount(me, "BufferPower");
                if (Debug && !_dumped) { _dumped = true; DumpDiagnostics(cs, players, localSlot, me); }
                ReadDots(me, items);
                long ps = Perf.Start();
                ReadSelfDamageCards(cs, players, localSlot, items);
                Perf.Stop("fc.selfcards", ps);
            }
        }
        catch { }

        try
        {
            foreach (var enemy in cs.Enemies)
            {
                if (enemy == null) continue;
                // Every creature still in the enemy list is read (the game removes the dead ones, see
                // ShouldCreatureBeRemovedFromCombatAfterDeath); one kept at 0 HP only counts for a death blow.
                bool alive = true;
                try { alive = enemy.IsAlive; } catch { }
                if (alive && DiesToPoisonBeforeActing(enemy)) continue;
                if (!TryReadEnemy(enemy, alive, items)) hasHidden = true;
            }
        }
        catch { }

        return new ForecastInputs(hp, maxHp, block, ostyAbsorb, hasHidden, items, endTurnBlock, ostyHp, buffer);
    }

    // A power's stack count on the creature (0 if absent or unreadable).
    private static int ReadPowerAmount(Creature c, string powerType)
    {
        try
        {
            if (GetProp(c, "Powers") is IEnumerable powers)
                foreach (var p in powers)
                    if (p != null && p.GetType().Name == powerType) return Math.Max(0, GetInt(p, "Amount"));
        }
        catch { }
        return 0;
    }

    // Mirrors the game's own check (NHealthBar.IsPoisonLethal, which turns the enemy's HP bar green):
    // HasPower<PoisonPower> and PoisonPower.CalculateTotalDamageNextTurn() >= CurrentHp. Poison ticks at the
    // start of the enemy's turn (PoisonPower.AfterSideTurnStart), before it acts, so its intents never land.
    // Doom is not treated this way: its kill timing relative to the enemy's attack isn't verified.
    private static bool DiesToPoisonBeforeActing(Creature enemy)
    {
        try
        {
            if (GetProp(enemy, "Powers") is not IEnumerable powers) return false;
            foreach (var p in powers)
            {
                if (p == null || p.GetType().Name != "PoisonPower") continue;
                int dmg = Invoke(p, "CalculateTotalDamageNextTurn") is int i ? i : 0;
                return dmg > 0 && dmg >= enemy.CurrentHp;
            }
        }
        catch { }
        return false;
    }

    // Block the player gains at the end of their turn, before the enemy turn, assuming they end the turn
    // now. Sources found by a call-graph sweep of sts2.dll v0.107.1 (end-of-turn hooks that call
    // CreatureCmd.GainBlock) and read from their IL:
    //   PlatingPower   BeforeSideTurnEndEarly: GainBlock(Amount)
    //   Orichalcum / FakeOrichalcum: VeryEarly arms only if Block <= 0, then GainBlock(DynamicVars.Block)
    //   CloakClasp     BeforeSideTurnEnd: GainBlock(cards in hand * DynamicVars.Block.BaseValue)
    //   FrostOrb       BeforeTurnEndOrbTrigger: GainBlock(PassiveVal, Focus included) per passive trigger
    //   RippleBasin    BeforeSideTurnEnd (per its text): gain DynamicVars.Block if no Attack was played this
    //                 turn, checked in the combat history (CardPlayStartedEntry.HappenedThisTurn); unreadable = 0.
    private static int ReadEndTurnBlock(CombatState cs, Player mePlayer, Creature me, int currentBlock)
    {
        int total = 0;
        try
        {
            if (GetProp(me, "Powers") is IEnumerable powers)
                foreach (var p in powers)
                    if (p != null && p.GetType().Name == "PlatingPower")
                        total += Math.Max(0, GetInt(p, "Amount"));
        }
        catch { }
        try
        {
            if (GetProp(mePlayer, "Relics") is IEnumerable relics)
                foreach (var rel in relics)
                {
                    if (rel == null) continue;
                    string tn = rel.GetType().Name;
                    if (tn != "Orichalcum" && tn != "FakeOrichalcum" && tn != "CloakClasp" && tn != "RippleBasin") continue;
                    object? blockVar = GetProp(rel, "DynamicVars") is { } dv ? GetProp(dv, "Block") : null;
                    int amount = blockVar != null ? GetNum(blockVar, "BaseValue", "Value", "IntValue") : 0;
                    if (amount <= 0) continue;
                    if (tn == "CloakClasp")
                    {
                        int handCount = 0;
                        if (FindHand(mePlayer, cs) is { } hand && GetProp(hand, "Cards") is IEnumerable cards)
                            foreach (var _ in cards) handCount++;
                        total += handCount * amount;
                    }
                    else if (tn == "RippleBasin")
                    {
                        // Only an affirmative "no Attack this turn" counts; if the history can't be read, add nothing.
                        if (PlayedAttackThisTurn(cs, mePlayer) == false) total += amount;
                    }
                    else if (currentBlock <= 0) total += amount;
                }
        }
        catch { }
        try { total += FrostOrbBlock(cs, mePlayer); } catch { }
        return total;
    }

    // Frost orbs gain their passive value as Block at end of turn (BeforeTurnEndOrbTrigger). PassiveVal
    // already includes Focus; the number of passive triggers per orb comes from the game's own hook
    // (Hook.ModifyOrbPassiveTriggerCount, e.g. relics that trigger an orb again), defaulting to 1.
    private static int FrostOrbBlock(CombatState cs, Player mePlayer)
    {
        object? pcs = GetProp(mePlayer, "PlayerCombatState");
        object? queue = pcs != null ? GetProp(pcs, "OrbQueue") : null;
        if (queue == null || GetProp(queue, "Orbs") is not IEnumerable orbs) return 0;
        int total = 0;
        foreach (var orb in orbs)
        {
            if (orb == null || orb.GetType().Name != "FrostOrb") continue;
            int value = GetNum(orb, "PassiveVal");
            if (value <= 0) continue;
            total += value * OrbPassiveTriggers(cs, orb);
        }
        return total;
    }

    private static MethodInfo? _orbTriggerHook;
    private static bool _orbTriggerHookLooked;
    private static int OrbPassiveTriggers(CombatState cs, object orb)
    {
        try
        {
            if (!_orbTriggerHookLooked)
            {
                _orbTriggerHookLooked = true;
                _orbTriggerHook = typeof(CombatState).Assembly.GetType("MegaCrit.Sts2.Core.Hooks.Hook")
                    ?.GetMethod("ModifyOrbPassiveTriggerCount", BindingFlags.Public | BindingFlags.Static);
            }
            if (_orbTriggerHook == null) return 1;
            var args = new object?[] { cs, orb, 1, null };
            if (_orbTriggerHook.Invoke(null, args) is int n) return Math.Clamp(n, 0, 10);
        }
        catch { }
        return 1;
    }

    // Whether the local player has started an Attack card this turn, from the game's combat history
    // (CardPlayStartedEntry, HappenedThisTurn). null when the history can't be read.
    private static bool? PlayedAttackThisTurn(CombatState cs, Player mePlayer)
    {
        try
        {
            var cm = CombatManager.Instance;
            object? history = (cm != null ? GetProp(cm, "History") : null) ?? GetProp(cs, "History");
            if (history == null || GetProp(history, "Entries") is not IEnumerable entries) return null;
            foreach (var e in entries)
            {
                if (e == null || e.GetType().Name != "CardPlayStartedEntry") continue;
                var happened = e.GetType().GetMethod("HappenedThisTurn", BindingFlags.Public | BindingFlags.Instance);
                if (happened == null) return null;
                if (happened.Invoke(e, new object[] { cs }) is not bool thisTurn || !thisTurn) continue;
                object? card = GetProp(e, "CardPlay") is { } play ? GetProp(play, "Card") : null;
                if (card == null) continue;
                object? owner = GetProp(card, "Owner");
                if (owner != null && !ReferenceEquals(owner, mePlayer)) continue;
                if (string.Equals(GetProp(card, "Type")?.ToString(), "Attack", StringComparison.Ordinal)) return true;
            }
            return false;
        }
        catch { return null; }
    }

    // True while the local player is actively taking their turn (PlayerTurnPhase.Play) — i.e. planning,
    // when the forecast is meaningful. During the enemy turn it reads false, so the caller freezes the
    // last planning value instead of letting it climb as block/Osty are consumed by resolving hits.
    // True while someone on the player side is still planning: the side is Player and at least one
    // player is in their Play phase. In co-op that includes after the local player has ended their turn
    // while teammates still play, so a teammate's stun, kill or debuff still updates the forecast. It
    // freezes once everyone has ended, before end-of-turn hooks run (Plating turning into block would
    // otherwise be counted twice: as block and as upcoming end-of-turn block). Falls back to the local
    // player's Play phase if the combat's current side can't be read.
    public static bool IsPlanningPhase(CombatState cs, IReadOnlyList<Player> players, int localSlot)
    {
        try
        {
            object? side = GetProp(cs, "CurrentSide");
            if (side != null)
            {
                if (!string.Equals(side.ToString(), "Player", StringComparison.OrdinalIgnoreCase)) return false;
                foreach (var p in players) if (InPlayPhase(p)) return true;
                return false;
            }
        }
        catch { }
        return localSlot >= 0 && localSlot < players.Count && InPlayPhase(players[localSlot]);
    }

    private static bool InPlayPhase(Player player)
    {
        try
        {
            object? pcs = GetProp(player, "PlayerCombatState");
            object? phase = pcs != null ? GetProp(pcs, "Phase") : null;
            return phase != null && string.Equals(phase.ToString(), "Play", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // Osty (Necrobinder's summon) is a damage sink in front of the player: its block + HP absorb attacks
    // before overflow reaches the player. Only counts while Osty is alive. `hp` is the HP part.
    private static int ReadOstyAbsorb(Player player, out int hp)
    {
        hp = 0;
        try
        {
            object? aliveObj = GetProp(player, "IsOstyAlive");
            if (aliveObj is bool alive && !alive) return 0;
            object? osty = GetProp(player, "Osty");
            if (osty == null) return 0;
            int ohp = GetInt(osty, "CurrentHp", "Hp", "Health");
            int oblock = GetInt(osty, "Block", "CurrentBlock");
            if (ohp <= 0 && oblock <= 0) return 0;
            hp = Math.Max(0, ohp);
            return Math.Max(0, ohp) + Math.Max(0, oblock);
        }
        catch { return 0; }
    }

    // Returns true if this enemy's intent was concretely readable (attack or a known non-attack move);
    // false when the intent is hidden/unreadable, so the caller raises the hidden flag. IntentRules decides
    // what each intent contributes (attacks, death blows like the Waterfall Giant's Explode, hidden ones).
    private static bool TryReadEnemy(Creature enemy, bool alive, List<ForecastItem> items)
    {
        var intents = ResolveIntents(enemy);
        if (intents.Count == 0) return !alive;

        string name = SafeName(enemy);
        bool readable = true;
        foreach (var intent in intents)
        {
            var threat = IntentRules.Classify(TypeChain(intent.GetType()), GetProp(intent, "IntentType")?.ToString());
            if (threat == IntentThreat.None) continue;   // buff/defend/etc: no HP threat
            int perHit = threat is IntentThreat.Attack or IntentThreat.DeathBlow ? ResolveIntentDamage(intent, enemy) : 0;
            var outcome = IntentRules.Resolve(threat, alive, perHit);
            if (Debug && threat == IntentThreat.DeathBlow) LogDeathBlow(enemy, intent, perHit);
            if (outcome == IntentRules.Outcome.Hidden) { readable = false; continue; }
            if (outcome != IntentRules.Outcome.Add) continue;

            int hits = GetInt(intent, "Repeats", "Hits", "Times", "Count", "AttackCount");
            if (hits <= 0) hits = 1;
            items.Add(new ForecastItem(name, ForecastKind.Attack, hits, perHit, blockable: true, conditional: false));
        }
        return readable;
    }

    // An intent's type name and its base types' names, most derived first (cached per type).
    private static readonly Dictionary<Type, string[]> _typeChains = new();
    private static string[] TypeChain(Type t)
    {
        if (_typeChains.TryGetValue(t, out var chain)) return chain;
        var names = new List<string>();
        for (var x = t; x != null && x != typeof(object); x = x.BaseType) names.Add(x.Name);
        return _typeChains[t] = names.ToArray();
    }

    // forecast_debug: what a death-blow intent looks like, once per enemy type/intent type/amount.
    private static readonly HashSet<string> _deathBlowLogged = new();
    private static void LogDeathBlow(Creature enemy, object intent, int perHit)
    {
        try
        {
            string key = $"{enemy.Monster?.GetType().Name}/{intent.GetType().Name}/{perHit}";
            if (!_deathBlowLogged.Add(key)) return;
            Godot.GD.Print($"[STS2 Damage] DIAG death blow {key} alive={enemy.IsAlive} hp={enemy.CurrentHp} chain={string.Join(">", TypeChain(intent.GetType()))}");
            DumpObj("death blow intent", intent);
        }
        catch { }
    }

    private static readonly System.Text.RegularExpressions.Regex NumRx = new(@"-?\d+");

    // The card's description with {Damage}-style templates resolved to real numbers. GetDescriptionForPile
    // needs the pile context to fill them in; the plain Description property does not.
    private static string? ResolveCardText(object card)
    {
        try
        {
            var type = card.GetType();
            if (!_describeMethod.TryGetValue(type, out var m))
            {
                foreach (var cand in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (cand.Name != "GetDescriptionForPile") continue;
                    var cps = cand.GetParameters();
                    if (cps.Length >= 1 && cps[0].ParameterType.IsEnum) { m = cand; break; }
                }
                _describeMethod[type] = m;
            }
            if (m == null) return null;

            var ps = m.GetParameters();
            var args = new object?[ps.Length];
            args[0] = Enum.Parse(ps[0].ParameterType, "Hand");   // PileType.Hand
            for (int i = 1; i < ps.Length; i++)
                args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                        : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);

            object? res = m.Invoke(card, args);
            string? s = res as string ?? (res != null ? TextHelper.SafeGetText(() => res) : null);
            return s != null ? TextHelper.StripRichTextTags(s) : null;
        }
        catch { return null; }
    }

    // The number the player sees, per hit. AttackIntent.GetSingleDamage(targets, self) is what the intent
    // shows: the base run through Hook.ModifyDamage against the local player (attacker Strength and Weak,
    // our Vulnerable, ...). DamageCalc alone is the raw base. A modified 0 is a real answer (e.g. Piercing
    // Wail's -6 Strength on a 5-damage attack) and must not fall through to the raw base, which over-reports.
    private static int ResolveIntentDamage(object intent, Creature enemy)
    {
        try
        {
            object? cs = GetProp(enemy, "CombatState");
            object? targets = cs != null ? GetProp(cs, "PlayerCreatures") : null;
            if (targets != null)
            {
                var single = FindMethod(intent, "GetSingleDamage", 2);
                if (single != null && single.Invoke(intent, new object[] { targets, enemy }) is int dmg) return Math.Max(0, dmg);

                var m = FindMethod(intent, "GetIntentLabel", 2);
                object? loc = m?.Invoke(intent, new object[] { targets, enemy });
                string? text = loc != null ? TextHelper.SafeGetText(() => loc) : null;
                if (!string.IsNullOrEmpty(text))
                {
                    var mm = NumRx.Match(text);
                    if (mm.Success && int.TryParse(mm.Value, out int n)) return Math.Max(0, n);
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

    // Card kind -> whether its text can cost the player HP. Card kinds are fixed for the game's lifetime.
    private static readonly Dictionary<Type, bool> _selfHarmByType = new();
    // Card kind -> its GetDescriptionForPile(PileType, ...) overload (null: none).
    private static readonly Dictionary<Type, MethodInfo?> _describeMethod = new();

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
    // stack/amount for stacking DoTs (Bleed/Poison); best-effort for others. Keyword DoTs bypass block; powers
    // found by their text ("take N damage") are blocked by the player's block but never by Osty.
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
                bool keyword = MatchesAny(tn, DotKeywords) || MatchesAny(id, DotKeywords);
                // Powers whose text tells the player to take damage / lose HP at end of turn (e.g.
                // Disintegration: "At the end of your turn, take 6 damage.") count too, whatever their name.
                string desc = TextHelper.SafeGetText(() => GetProp(p, "Description")) ?? "";
                bool byText = SelfHarmText.TryMatch(desc, out int textAmount, out bool blockable)
                              && Contains(desc, "end of your turn");
                if (!keyword && !byText) continue;
                int amount = GetInt(p, "Amount", "Stacks", "Count", "Value");
                if (byText && textAmount > 0) amount = textAmount;
                if (amount <= 0) continue;
                string label = TextHelper.SafeGetText(() => GetProp(p, "Title")) ?? Prettify(tn);
                // Keyword DoTs (poison, bleed, ...) bypass block; text-detected ones follow their wording.
                items.Add(new ForecastItem(label, ForecastKind.Dot, 1, amount, blockable: byText && !keyword && blockable, conditional: false));
            }
        }
        catch { }
    }

    // Cards in hand that cost the player HP. Two flavors:
    //   • Infection-style cards that bite at end of turn while held → GUARANTEED loss (counts toward min).
    //   • Cards that only cost HP when played → CONDITIONAL (widens the max only).
    // Amount comes from the self clause of the resolved description, falling back to the card's fields.
    private static void ReadSelfDamageCards(CombatState cs, IReadOnlyList<Player> players, int localSlot, List<ForecastItem> items)
    {
        try
        {
            if (localSlot < 0 || localSlot >= players.Count) return;
            object? hand = FindHand(players[localSlot], cs);
            if (hand == null) return;
            object? cardsObj = GetProp(hand, "Cards") ?? hand; // hand is a CardPile; its list is under .Cards
            if (cardsObj is not IEnumerable cards) return;
            if (Debug) { long pd = Perf.Start(); DebugHand(cards); Perf.Stop("fc.debughand", pd); }
            foreach (var card in cards)
            {
                if (card == null) continue;
                var type = card.GetType();
                string tn = type.Name;
                // Whether a card can cost HP is fixed by its kind, so building its text (expensive: the game
                // formats it and runs every damage modifier) is only needed for the few that can (issue #31).
                if (_selfHarmByType.TryGetValue(type, out bool harms) && !harms) continue;
                // GetDescriptionForPile(Hand) resolves the {Damage} template to the real number; the raw
                // Description LocString leaves it as "{Damage:diff()}".
                string desc = ResolveCardText(card)
                    ?? TextHelper.SafeGetText(() => GetProp(card, "Description") ?? GetProp(card, "Text")) ?? "";

                // Self-harm cards tell the player to "take N damage" / "lose N HP" (SelfHarmText: only when the
                // player is the subject, not "they take 9 damage"). The subject is fixed by the card's kind; only
                // the number varies (upgrades, modifiers), so the per-type verdict stays valid.
                bool selfHarm = SelfHarmText.TryMatch(desc, out int amount, out bool blockable);
                if (desc.Length > 0) _selfHarmByType[type] = selfHarm;   // an unresolved text isn't a verdict
                if (!selfHarm) continue;

                // Amount: the number in the self clause of the resolved text; fall back to fields.
                if (amount <= 0) amount = GetNum(card, "CalculatedDamage", "HpLoss", "Damage", "BaseDamage");
                if (amount <= 0) continue;

                string label = TextHelper.SafeGetText(() => GetProp(card, "Title")) ?? tn;
                // "if this is in your hand" / end-of-turn bite = guaranteed loss; otherwise conditional on play.
                bool held = Contains(desc, "in your hand") || Contains(desc, "end of your turn") || Contains(tn, "Infection");
                if (held)
                    items.Add(new ForecastItem(label, ForecastKind.Dot, 1, amount, blockable, conditional: false));
                else
                    items.Add(new ForecastItem(label, ForecastKind.SelfCard, 1, amount, blockable, conditional: true));
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
