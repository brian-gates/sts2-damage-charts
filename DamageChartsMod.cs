using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_DamageCharts;

// Standalone in-combat damage analytics overlay. Reads game state directly; no dependency on
// any other mod. Capture is event-driven (Creature.CurrentHpChanged) for amounts, with a single
// synchronous Harmony Prefix on CreatureCmd.Damage to attribute each hit to its source (card name
// or, for powers/relics/orbs, the calling type resolved from the stack) and dealer.
[ModInitializer("Initialize")]
public static class DamageChartsMod
{
    private const string ConfigFileName = "STS2_DamageCharts.conf";
    private const string BuildTag = "2026-06-21";  // stamped at init so logs identify the loaded DLL

    private static readonly Color[] Palette = UiTheme.Players;

    private static bool _initialized;
    private static bool _disabled;            // hard kill-switch: once set, nothing runs again
    private static Callable _tickCallable;     // stored so we can disconnect ProcessFrame on disable
    private static readonly string[] HarmonyIds =
        { "com.sts2damagecharts", "com.sts2damagecharts.suppress", "com.sts2damagecharts.deck", "com.sts2damagecharts.powers" };
    private static bool _showBars = true;

    private static readonly DamageTracker _tracker = new();
    private static readonly RunAggregate _run = new();
    private static bool _runActiveLast;
    private static readonly Dictionary<Creature, Action<int, int>> _subs = new();
    private static readonly Dictionary<Creature, Action<int, int>> _blockSubs = new();
    private static readonly Dictionary<Creature, (string Label, Creature? Dealer, string? Icon)> _pending = new();
    // Set by a power's hook just before it deals damage; consumed by the next non-card hit. Only valid
    // for ~1 frame (a damaging hook stamps-and-deals synchronously) so a stale stamp from a passive
    // relic hook can't mislabel a later unrelated hit (e.g. a potion).
    private static (string Label, string? Icon)? _activeSource;
    private static long _frame;
    private static long _activeFrame;
    private static string? _doomIcon;
    private static IReadOnlyList<Player> _players = Array.Empty<Player>();
    private static bool _combatLast;
    private static int _errCount;

    private static DamageChartView? _bars;
    private static DamageDetailView? _detail;
    private static DamageTooltipView? _tooltip;
    private static bool _detailVisible;
    private static bool _summaryActive;       // auto end-of-combat summary showing
    private static bool _summaryOnEnd = true; // config
    private static bool _recapVisible;        // on-demand run recap takeover showing (out of combat)
    private static bool _recapEnabled = true; // config
    private static float _uiScale = 1f;       // config: extra font/spacing multiplier for the takeover
    // Power/relic/orb/potion source-attribution Harmony hooks. Default OFF: the blanket patching of every
    // hook method (~791) can intermittently deadlock the CLR's reflection/JIT (observed as a hard hang and
    // "Bad IL range" errors). With it off, DoT/relic damage is labeled "Status"/"Other" but the mod is
    // stable. Opt in via config to get by-name attribution at that risk. See issue tracker for the fix.
    private static bool _sourceHooks;
    private static bool _combatResolved;      // this combat reached a win/loss (not abandoned)
    private static int _localSlot;            // local player's slot for this combat

    // Hotkey (default bare C, "combat stats"). A/S/D/X/M/E are taken by the game; C is free.
    private static Key _hotkeyKey = Key.C;
    private static readonly List<Key> _hotkeyMods = new();
    private static string _hotkeySpec = "c";
    private static bool _hotkeyDownLast;

    // Saved chart position (null = default top-right until first placed/dragged).
    private static float? _posXFrac;
    private static float? _posYFrac;

    private static long _lastConfigWriteTicks;

    public static void Initialize()
    {
        try
        {
            if (!LoadConfig()) { GD.Print("[STS2 Damage] disabled via config"); return; }

            var tree = (SceneTree)Engine.GetMainLoop();
            _tickCallable = Callable.From(Tick);
            tree.Connect(SceneTree.SignalName.ProcessFrame, _tickCallable);

            TryApplyDamageHook();
            TryApplySuppressHook();
            if (_sourceHooks) TryApplyPowerSourceHooks();
            else GD.Print("[STS2 Damage] power-source hooks DISABLED via config");
            _initialized = true;
            GD.Print($"[STS2 Damage] initialized (build {BuildTag}, source_hooks={_sourceHooks})");
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] init failed: {ex.Message}"); }
    }

    // ===== Config =====

    private static string? ConfigPath()
    {
        string? dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        return dir == null ? null : Path.Combine(dir, ConfigFileName);
    }

    private static void RememberConfigWriteTime(string path)
    {
        try { _lastConfigWriteTicks = File.GetLastWriteTimeUtc(path).Ticks; } catch { }
    }

    private static bool LoadConfig()
    {
        try
        {
            string? path = ConfigPath();
            if (path == null) return true;

            if (!File.Exists(path))
            {
                try { File.WriteAllText(path, "{\n  \"enabled\": true,\n  \"hotkey\": \"c\",\n  \"show_bars\": true\n}\n"); }
                catch { }
                RememberConfigWriteTime(path);
                return true;
            }

            RememberConfigWriteTime(path);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False) return false;
            if (root.TryGetProperty("show_bars", out var sb) && sb.ValueKind == JsonValueKind.False) _showBars = false;
            if (root.TryGetProperty("summary_on_combat_end", out var su) && su.ValueKind == JsonValueKind.False) _summaryOnEnd = false;
            if (root.TryGetProperty("run_recap", out var rr) && rr.ValueKind == JsonValueKind.False) _recapEnabled = false;
            if (root.TryGetProperty("ui_scale", out var us) && us.ValueKind == JsonValueKind.Number && us.TryGetSingle(out float uss)) _uiScale = Math.Clamp(uss, 0.5f, 4f);
            if (root.TryGetProperty("source_hooks", out var shk) && shk.ValueKind == JsonValueKind.True) _sourceHooks = true;
            if (root.TryGetProperty("hotkey", out var hk) && hk.ValueKind == JsonValueKind.String)
            {
                _hotkeySpec = hk.GetString() ?? _hotkeySpec;
                ParseHotkey(_hotkeySpec);
            }
            if (root.TryGetProperty("pos_x_frac", out var px) && px.TryGetSingle(out var fx)) _posXFrac = fx;
            if (root.TryGetProperty("pos_y_frac", out var py) && py.TryGetSingle(out var fy)) _posYFrac = fy;
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] config parse failed, using defaults: {ex.Message}"); }
        return true;
    }

    // Persist position (and current settings) when the chart is dragged to a new spot.
    private static void SaveConfig()
    {
        try
        {
            string? path = ConfigPath();
            if (path == null || _bars == null) return;
            var pos = _bars.PositionFraction;
            var dict = new Dictionary<string, object>
            {
                ["enabled"] = true,
                ["hotkey"] = _hotkeySpec,
                ["show_bars"] = _showBars,
                ["summary_on_combat_end"] = _summaryOnEnd,
                ["pos_x_frac"] = (float)Math.Round(pos.X, 4),
                ["pos_y_frac"] = (float)Math.Round(pos.Y, 4),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
            RememberConfigWriteTime(path);
            _posXFrac = pos.X; _posYFrac = pos.Y;
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] save config failed: {ex.Message}"); }
    }

    private static void ReloadConfigIfChangedOnDisk()
    {
        try
        {
            string? path = ConfigPath();
            if (path == null || !File.Exists(path)) return;
            long writeTicks = File.GetLastWriteTimeUtc(path).Ticks;
            if (writeTicks == _lastConfigWriteTicks) return;
            _lastConfigWriteTicks = writeTicks;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("hotkey", out var hk) && hk.ValueKind == JsonValueKind.String)
            {
                string? spec = hk.GetString();
                if (!string.IsNullOrWhiteSpace(spec) && spec != _hotkeySpec)
                {
                    _hotkeySpec = spec!;
                    ParseHotkey(_hotkeySpec);
                    _hotkeyDownLast = false;
                    GD.Print($"[STS2 Damage] hotkey reloaded live: {_hotkeySpec}");
                }
            }
            bool showBars = !(root.TryGetProperty("show_bars", out var sb) && sb.ValueKind == JsonValueKind.False);
            if (showBars != _showBars)
            {
                _showBars = showBars;
                if (!_showBars) _bars?.Hide();
            }
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] config reload failed: {ex.Message}"); }
    }

    private static Vector2 RootMouse()
    {
        try { return ((SceneTree)Engine.GetMainLoop()).Root.GetMousePosition(); }
        catch { return Vector2.Zero; }
    }

    private static void ParseHotkey(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return;
        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return;

        var mods = new List<Key>();
        Key main = _hotkeyKey;
        foreach (var raw in parts)
        {
            string p = raw.ToLowerInvariant();
            switch (p)
            {
                case "cmd": case "meta": case "win": case "super": mods.Add(Key.Meta); break;
                case "shift": mods.Add(Key.Shift); break;
                case "alt": case "option": case "opt": mods.Add(Key.Alt); break;
                case "ctrl": case "control": mods.Add(Key.Ctrl); break;
                default:
                    if (Enum.TryParse<Key>(raw.ToUpperInvariant(), out var k)) main = k;
                    break;
            }
        }
        _hotkeyKey = main;
        _hotkeyMods.Clear();
        _hotkeyMods.AddRange(mods);
    }

    // ===== Harmony damage hook =====

    private static void TryApplyDamageHook()
    {
        try
        {
            var method = ResolveDamageMethod();
            if (method == null) { GD.Print("[STS2 Damage] damage hook not resolved; dealt attribution will use active player"); return; }
            var prefix = new HarmonyMethod(typeof(DamageChartsMod).GetMethod(nameof(DamagePrefix), BindingFlags.NonPublic | BindingFlags.Static));
            new Harmony("com.sts2damagecharts").Patch(method, prefix: prefix);
            GD.Print("[STS2 Damage] damage dealer hook applied");
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] damage hook skipped: {ex.GetType().Name}: {ex.Message}"); }
    }

    // The game's hotkey dispatcher (NHotkeyManager._UnhandledInput) opens the deck/pile views on
    // bare keys like D with loose modifier matching, so Cmd+D would still open the deck. Patch it to
    // swallow the event when it matches OUR exact combo (key + required modifiers). Plain D is
    // untouched; the toggle itself is driven by polling in HandleHotkey().
    private static void TryApplySuppressHook()
    {
        // (a) Central hotkey dispatcher — swallow our exact combo so it can't trigger generic shortcuts.
        try
        {
            var method = AccessTools.Method(typeof(NHotkeyManager), "_UnhandledInput");
            if (method != null)
            {
                var prefix = new HarmonyMethod(typeof(DamageChartsMod).GetMethod(nameof(SuppressPrefix), BindingFlags.NonPublic | BindingFlags.Static));
                new Harmony("com.sts2damagecharts.suppress").Patch(method, prefix: prefix);
                GD.Print("[STS2 Damage] hotkey suppress hook applied");
            }
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] hotkey suppress hook skipped: {ex.GetType().Name}: {ex.Message}"); }

        // (b) Block only the deck-open itself (NDeckViewScreen.ShowScreen) when our modifier is held.
        // This exists ONLY to stop a D-based hotkey (e.g. cmd+d) from also opening the deck — so it is
        // installed only when our hotkey actually uses the D key. With any other hotkey (the default is
        // bare C) there is no collision, and patching ShowScreen at all would needlessly risk the deck
        // not opening, so we skip it entirely.
        if (_hotkeyKey != Key.D) { GD.Print("[STS2 Damage] deck-open suppress hook not needed (hotkey isn't D)"); return; }
        // We deliberately do NOT patch the button's OnRelease: that handler also resets the button's
        // press animation, so skipping it leaves the button stuck mid-rotation. Letting OnRelease run
        // normally (press→release animation, no deck) while no-opping ShowScreen looks clean.
        try
        {
            var deckScreen = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen");
            var showScreen = deckScreen != null ? AccessTools.Method(deckScreen, "ShowScreen") : null;
            if (showScreen == null) { GD.Print("[STS2 Damage] deck-open suppress hook not resolved"); return; }
            var dp = new HarmonyMethod(typeof(DamageChartsMod).GetMethod(nameof(DeckOpenPrefix), BindingFlags.NonPublic | BindingFlags.Static));
            new Harmony("com.sts2damagecharts.deck").Patch(showScreen, prefix: dp);
            GD.Print("[STS2 Damage] deck-open suppress hook applied");
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] deck-open suppress hook skipped: {ex.GetType().Name}: {ex.Message}"); }
    }

    // Skip opening the deck view when our hotkey's modifier(s) are physically held (i.e. Cmd+D).
    // Returning false skips the original; __result stays null, which callers tolerate.
    private static bool DeckOpenPrefix()
    {
        try { if (OurModifiersHeld()) return false; } catch { }
        return true;
    }

    private static bool OurModifiersHeld()
    {
        if (_hotkeyMods.Count == 0) return false;
        foreach (var m in _hotkeyMods) if (!Input.IsPhysicalKeyPressed(m)) return false;
        return true;
    }

    // Return false to skip the game's hotkey dispatch for events that match our combo.
    // Also the one place mouse-wheel events reach us (no engine GUI callbacks on our CanvasLayer):
    // while the breakdown is up, route unhandled wheel notches into its scroll and swallow them.
    private static bool SuppressPrefix(InputEvent inputEvent)
    {
        try
        {
            if (inputEvent is InputEventKey k && MatchesOurCombo(k)) return false;
            if (inputEvent is InputEventMouseButton mb && mb.Pressed && (_detailVisible || _summaryActive || _recapVisible) && _detail != null)
            {
                if (mb.ButtonIndex == MouseButton.WheelUp) { _detail.ScrollBy(-3); return false; }
                if (mb.ButtonIndex == MouseButton.WheelDown) { _detail.ScrollBy(3); return false; }
            }
        }
        catch { }
        return true;
    }

    private static bool MatchesOurCombo(InputEventKey k)
    {
        if (k.PhysicalKeycode != _hotkeyKey && k.Keycode != _hotkeyKey) return false;
        foreach (var m in _hotkeyMods)
        {
            bool held = m switch
            {
                Key.Meta => k.MetaPressed,
                Key.Shift => k.ShiftPressed,
                Key.Alt => k.AltPressed,
                Key.Ctrl => k.CtrlPressed,
                _ => true,
            };
            if (!held) return false;
        }
        return true;
    }

    // The central overload: the unique 6-param Damage whose 2nd param is IEnumerable<Creature>.
    private static MethodBase? ResolveDamageMethod()
    {
        foreach (var m in typeof(MegaCrit.Sts2.Core.Commands.CreatureCmd).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "Damage") continue;
            var ps = m.GetParameters();
            if (ps.Length == 6 && ps[1].ParameterType == typeof(IEnumerable<Creature>)) return m;
        }
        return null;
    }

    // Patch every PowerModel subclass's turn-start hook (where DoTs like Poison deal damage). The
    // prefix reads the power's own Title/icon from __instance — no stack walking — and stashes it as
    // the active source for the Damage call that immediately follows.
    private static void TryApplyPowerSourceHooks()
    {
        try
        {
            var bases = new List<Type>();
            foreach (var name in new[]
            {
                "MegaCrit.Sts2.Core.Models.PowerModel",  // Poison, Thorns, Black Hole, ...
                "MegaCrit.Sts2.Core.Models.RelicModel",  // Potion Shaped Rock, ...
                "MegaCrit.Sts2.Core.Models.OrbModel",    // Lightning, ...
                "MegaCrit.Sts2.Core.Models.PotionModel", // attack potions, ...
            })
            {
                var bt = AccessTools.TypeByName(name);
                if (bt != null) bases.Add(bt);
            }
            if (bases.Count == 0) { GD.Print("[STS2 Damage] source base types not found; non-card names disabled"); return; }

            var prefix = new HarmonyMethod(typeof(DamageChartsMod).GetMethod(nameof(PowerSourcePrefix), BindingFlags.NonPublic | BindingFlags.Static));
            var doomPrefix = new HarmonyMethod(typeof(DamageChartsMod).GetMethod(nameof(DoomKillPrefix), BindingFlags.NonPublic | BindingFlags.Static));
            try { var dp = "res://images/atlases/power_atlas.sprites/doom.tres"; if (ResourceLoader.Exists(dp)) _doomIcon = dp; } catch { }
            var h = new Harmony("com.sts2damagecharts.powers");

            Type[] types;
            try { types = typeof(CombatManager).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

            // Patch every overridden hook method (void/Task) on each power subclass — e.g.
            // AfterSideTurnStart (Poison), BeforeDamageReceived (Thorns), AfterCardPlayed /
            // AfterStarsGained (Black Hole), etc. The prefix stamps that power as the active source
            // for the Damage call that follows, so all damaging powers get named without a hardcoded list.
            int n = 0;
            foreach (var t in types)
            {
                if (t == null || t.IsAbstract) continue;
                bool isSource = false;
                foreach (var bt in bases) if (t.IsSubclassOf(bt)) { isSource = true; break; }
                if (!isSource) continue;

                // Doom kills via CreatureCmd.Kill (animation-delayed) — pre-tag the doomed creatures by
                // reference so the kill-time HP loss is labeled "Doom" regardless of timing.
                if (t.Name == "DoomPower")
                {
                    var dk = AccessTools.DeclaredMethod(t, "DoomKill");
                    if (dk != null) { try { h.Patch(dk, prefix: doomPrefix); } catch { } }
                }

                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    try
                    {
                        if (m.IsSpecialName || m.IsAbstract || m.IsGenericMethod) continue;
                        var baseDecl = m.GetBaseDefinition().DeclaringType;
                        if (baseDecl == m.DeclaringType || baseDecl == typeof(object)) continue; // only real hook overrides
                        var rt = m.ReturnType;
                        if (rt != typeof(void) && !typeof(System.Threading.Tasks.Task).IsAssignableFrom(rt)) continue;
                        h.Patch(m, prefix: prefix);
                        n++;
                    }
                    catch { }
                }
            }
            GD.Print($"[STS2 Damage] power-source hooks applied to {n} methods");
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] power-source hooks skipped: {ex.Message}"); }
    }

    // Pre-tag doomed creatures so their kill-time HP loss is attributed to "Doom".
    private static void DoomKillPrefix(System.Collections.Generic.IReadOnlyList<Creature> creatures)
    {
        try
        {
            if (creatures == null) return;
            foreach (var c in creatures) if (c != null) _pending[c] = ("Doom", null, _doomIcon);
        }
        catch { }
    }

    private static void PowerSourcePrefix(object __instance)
    {
        try
        {
            var t = __instance.GetType();
            string? label = TextHelper.SafeGetText(() => t.GetProperty("Title")?.GetValue(__instance));
            if (string.IsNullOrEmpty(label)) return;
            string? icon = null;
            foreach (var prop in new[] { "PackedIconPath", "ImagePath" }) // powers/relics/orbs use PackedIconPath; potions use ImagePath
            {
                try { if (t.GetProperty(prop)?.GetValue(__instance) is string p && ResourceLoader.Exists(p)) { icon = p; break; } }
                catch { }
            }
            _activeSource = (label!, icon);
            _activeFrame = _frame;
        }
        catch { }
    }

    private static void DamagePrefix(IEnumerable<Creature> targets, Creature dealer, CardModel cardSource)
    {
        try
        {
            if (targets == null) return;
            string label;
            string? icon = null;
            if (cardSource != null)
            {
                label = TextHelper.SafeGetText(() => cardSource.Title) ?? "Card";
                try { if (cardSource.HasPortrait) icon = cardSource.PortraitPath; } catch { }
            }
            else if (dealer != null && dealer.IsPet)
            {
                // Pet/summon attack (e.g. Osty) — credit the pet by name (the commanding card isn't on this call).
                label = TextHelper.SafeGetText(() => dealer.Monster!.Title) ?? "Pet";
            }
            else if (ActiveFresh())
            {
                // A power's hook tagged this damage just before calling Damage (e.g. Poison). Only used
                // when fresh (~same frame) so a stale passive-relic stamp can't mislabel this hit.
                label = _activeSource!.Value.Label;
                icon = _activeSource.Value.Icon;
                _activeSource = null; // consume
            }
            else label = "Status"; // unattributed non-card damage
            foreach (var t in targets)
                if (t != null) _pending[t] = (label, dealer, icon);
        }
        catch { /* never disrupt damage resolution */ }
    }

    // ===== Per-frame tick =====

    private static void Tick()
    {
        if (_disabled || !_initialized) return;
        try
        {
            _frame++;
            if (_frame % 30 == 0) ReloadConfigIfChangedOnDisk();
            if (_frame > 120 && _frame % 30 == 0)
            {
                try { UiTheme.EnsurePanelStyle(((SceneTree)Engine.GetMainLoop()).Root); } catch { }
            }
            HandleHotkey();

            // Run lifecycle: a fresh run (not-in-progress -> in-progress) clears the run-wide aggregate
            // so the recap never shows a previous run's data.
            bool runActive = SafeIsRunInProgress();
            if (runActive && !_runActiveLast) { _run.Clear(); _recapVisible = false; }
            _runActiveLast = runActive;

            bool inCombat = SafeIsInCombat();
            // Edge handlers are contained AND state advances regardless, so a throw here can never
            // retry every frame (which previously stormed Godot's exception logger into a beachball).
            if (inCombat != _combatLast)
            {
                try { if (inCombat) OnCombatStart(); else OnCombatEnd(); }
                catch (Exception ex) { GD.PrintErr($"[STS2 Damage] lifecycle error: {ex.Message}"); }
                _combatLast = inCombat;
            }

            if (inCombat)
            {
                var cs = SafeCombatState();
                if (cs != null)
                {
                    SubscribeNewCreatures(cs); // keep tracking even while hidden
                    // Sticky: did this combat reach a real conclusion (all enemies dead, or local player
                    // dead)? If the player abandons the fight instead, this stays false and we suppress the
                    // end-of-combat summary.
                    if (!_combatResolved && IsCombatResolved(cs)) _combatResolved = true;
                    if (IsBlockingUiOpen())
                    {
                        // A game menu/modal/overlay is on top — get out of its way.
                        _bars?.Hide(); _tooltip?.Hide(); _detail?.SetVisible(false);
                    }
                    else if (_detailVisible && _detail != null)
                    {
                        // Full-screen breakdown is up — it takes over; hide the compact bars/tooltip.
                        _bars?.Hide(); _tooltip?.Hide();
                        _detail.SummaryMode = false; _detail.RecapMode = false; _detail.UiScaleMul = _uiScale;
                        _detail.ShowRunHistory = true;
                        _detail.RunFights = _run.HasData() ? _run.EncounterStats(_localSlot) : null;
                        _detail.SetVisible(true);
                        _detail.Render(_tracker.SourceSnapshot(), _tracker.Snapshot(), Palette);
                        _detail.UpdateMouse(Input.IsMouseButtonPressed(MouseButton.Left));
                        if (_detail.TakeCloseRequest()) { _detailVisible = false; _detail.SetVisible(false); } // ✕ clicked

                    }
                    else if (_combatResolved)
                    {
                        // The fight is won/lost (or winding down) — drop the always-on HUD now rather than
                        // leaving it up through the victory/defeat sequence until combat formally ends.
                        _bars?.Hide(); _tooltip?.Hide(); _detail?.SetVisible(false);
                    }
                    else
                    {
                        _detail?.SetVisible(false);
                        if (_showBars && _bars != null)
                        {
                            _bars.UiScaleMul = _uiScale;
                            _bars.Render(_tracker.Snapshot(), Palette);
                            // Poll-based drag + click + hover (no input overrides).
                            if (_bars.UpdateMouse(Input.IsMouseButtonPressed(MouseButton.Left))) SaveConfig();
                            if (_bars.TakeClick()) { _detailVisible = !_detailVisible; _detail?.SetVisible(_detailVisible); } // click chart → toggle details
                            if (!_bars.IsDragging && _bars.TryGetHover(out var hv)) _tooltip?.Render(hv, RootMouse());
                            else _tooltip?.Hide();
                        }
                    }
                }
            }
            else if (_recapVisible && _recapEnabled && _run.HasData() && _detail != null)
            {
                // On-demand run recap: a full-screen takeover shown out of combat (e.g. over the run's
                // victory/death screen). Unlike the in-combat takeover this is NOT gated by blocking UI,
                // so it can appear on top of the game's end screen.
                _bars?.Hide(); _tooltip?.Hide(); _summaryActive = false;
                _detail.SummaryMode = false; _detail.RecapMode = true; _detail.ShowRunHistory = false; _detail.UiScaleMul = _uiScale; _detail.SetVisible(true);
                _detail.Render(_run.RunSourceSnapshot(), _run.RunChartSnapshot(), Palette);
                _detail.UpdateMouse(Input.IsMouseButtonPressed(MouseButton.Left));
                if (_detail.TakeCloseRequest()) { _recapVisible = false; _detail.RecapMode = false; _detail.SetVisible(false); }
            }
            else if (_summaryActive && _detail != null)
            {
                // End-of-combat summary: shown over the rewards moment (click-through), hidden while a
                // menu is up, dismissed once the player moves on to the map (or via Cmd+D / next fight).
                _bars?.Hide(); _tooltip?.Hide();
                _detail.RecapMode = false; _detail.ShowRunHistory = false;
                if (IsMapOpen()) { _summaryActive = false; _detail.SetVisible(false); }
                else if (IsMenuOpen()) { _detail.SetVisible(false); }
                else
                {
                    _detail.SummaryMode = true; _detail.SetVisible(true);
                    _detail.Render(_tracker.SourceSnapshot(), _tracker.Snapshot(), Palette);
                    _detail.UpdateMouse(Input.IsMouseButtonPressed(MouseButton.Left));
                    if (_detail.TakeCloseRequest()) { _summaryActive = false; _detail.SetVisible(false); } // ✕ clicked
                }
            }

            _errCount = 0;
        }
        catch (Exception ex)
        {
            // Disable fast on repeated errors so we never spam the broken logger or stall the game.
            if (++_errCount >= 5) HardDisable($"repeated errors: {ex.Message}");
        }
    }

    // Permanent kill-switch. Stops the per-frame tick AND removes our Harmony patches so any faulting
    // woven method (e.g. a "Bad IL range" power hook) reverts to the game's original IL — otherwise the
    // game keeps invoking the broken method every frame and the failsafe can't actually contain it.
    private static void HardDisable(string reason)
    {
        if (_disabled) return;
        _disabled = true;
        _initialized = false;
        try { GD.PrintErr($"[STS2 Damage] disabled: {reason}"); } catch { }
        try { ((SceneTree)Engine.GetMainLoop()).Disconnect(SceneTree.SignalName.ProcessFrame, _tickCallable); } catch { }
        foreach (var id in HarmonyIds) { try { new Harmony(id).UnpatchAll(id); } catch { } }
        SafeTeardown();
    }

    private static void HandleHotkey()
    {
        bool down = Input.IsPhysicalKeyPressed(_hotkeyKey);
        foreach (var m in _hotkeyMods) down = down && Input.IsPhysicalKeyPressed(m);
        if (down && !_hotkeyDownLast)
        {
            if (SafeIsInCombat())
            {
                _detailVisible = !_detailVisible; _detail?.SetVisible(_detailVisible);
            }
            else if (_summaryActive)
            {
                // The end-of-combat summary takes priority: the hotkey dismisses it (next press opens recap).
                _summaryActive = false; _detail?.SetVisible(false);
            }
            else if (_recapEnabled && _run.HasData())
            {
                _recapVisible = !_recapVisible;
                if (!_recapVisible) _detail?.SetVisible(false);
            }
            else { _detailVisible = !_detailVisible; _detail?.SetVisible(_detailVisible); }
        }
        _hotkeyDownLast = down;
    }

    // ===== Combat lifecycle =====

    private static void OnCombatStart()
    {
        var rs = SafeRunState();
        _players = rs?.Players ?? (IReadOnlyList<Player>)Array.Empty<Player>();
        if (_players.Count == 0) return;

        var labels = new string[_players.Count];
        Player? me = rs != null ? LocalContext.GetMe(rs) : null;
        int localSlot = 0;
        for (int i = 0; i < _players.Count; i++)
        {
            string name = TextHelper.SafeGetText(() => _players[i].Character.Title) ?? $"P{i + 1}";
            bool isMe = ReferenceEquals(_players[i], me);
            if (isMe) localSlot = i;
            labels[i] = isMe && _players.Count > 1 ? $"{name} (You)" : name;
        }

        _summaryActive = false; // a new fight clears any lingering end-of-combat summary
        _recapVisible = false;   // the recap is an out-of-combat view
        _combatResolved = false; // until enemies/player actually die, this combat hasn't ended naturally
        _localSlot = localSlot;
        _tracker.Reset(_players.Count, labels, localSlot);
        _pending.Clear();
        UnsubscribeAll();
        EnsureViews();
        GD.Print($"[STS2 Damage] combat start ({_players.Count} player(s))");
    }

    private static void OnCombatEnd()
    {
        UnsubscribeAll();
        _pending.Clear();
        _bars?.Hide();
        _tooltip?.Hide();
        // Fold this combat into the run-wide aggregate before the next combat's Reset() wipes the tracker.
        if (_tracker.HasData()) _run.Fold(_tracker.SourceSnapshot(), _tracker.Snapshot());
        // Auto-show the breakdown as an end-of-combat summary (until dismissed / map / next fight), but
        // only for a real win/loss — not when the player abandons combat (e.g. quits to the menu).
        _summaryActive = _summaryOnEnd && _combatResolved && _tracker.HasData();
        if (!_summaryActive) _detail?.SetVisible(false);
        GD.Print($"[STS2 Damage] combat end (summary={_summaryActive})");
    }

    private static void EnsureViews()
    {
        try
        {
            var root = ((SceneTree)Engine.GetMainLoop()).Root;
            UiTheme.EnsureFont(root);
            UiTheme.EnsurePanelStyle(root);
            Vector2? savedPos = (_posXFrac.HasValue && _posYFrac.HasValue) ? new Vector2(_posXFrac.Value, _posYFrac.Value) : null;
            if (_bars == null || !_bars.IsValid()) _bars = new DamageChartView(root, savedPos);
            // _detail may have been freed by the game (its content can be reparented under NGlobalUi).
            if (_detail != null && !_detail.IsValid()) { _detail.Dispose(); _detail = null; }
            if (_detail == null) _detail = new DamageDetailView(root);
            if (_tooltip == null || !_tooltip.IsValid()) _tooltip = new DamageTooltipView(root);
            if (_detail != null) { _detail.HotkeyHint = _hotkeySpec.ToUpperInvariant(); _detail.UiScaleMul = _uiScale; }
            if (_bars != null) _bars.UiScaleMul = _uiScale;
            _detail?.SetVisible(_detailVisible);
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] failed to create views: {ex.Message}"); }
    }

    // ===== Capture =====

    private static void SubscribeNewCreatures(CombatState cs)
    {
        for (int i = 0; i < _players.Count; i++)
        {
            var pc = _players[i].Creature;
            if (pc != null) Subscribe(pc);
        }
        try { foreach (var e in cs.Enemies) if (e != null) Subscribe(e); }
        catch { /* enemy list may mutate; catch stragglers next frame */ }
    }

    private static void Subscribe(Creature creature)
    {
        if (_subs.ContainsKey(creature)) return;
        Action<int, int> handler = (oldHp, newHp) => OnHpChanged(creature, oldHp, newHp);
        creature.CurrentHpChanged += handler;
        _subs[creature] = handler;
        // Block gained is captured the same event-driven way as HP: sum positive BlockChanged deltas.
        Action<int, int> bh = (oldBlock, newBlock) => OnBlockChanged(creature, oldBlock, newBlock);
        try { creature.BlockChanged += bh; _blockSubs[creature] = bh; } catch { }
    }

    private static void UnsubscribeAll()
    {
        foreach (var kv in _subs) { try { kv.Key.CurrentHpChanged -= kv.Value; } catch { } }
        _subs.Clear();
        foreach (var kv in _blockSubs) { try { kv.Key.BlockChanged -= kv.Value; } catch { } }
        _blockSubs.Clear();
    }

    // Did this combat reach a natural conclusion (win/lose) rather than being abandoned (quit to menu)?
    // The game's own end-of-combat flags are the reliable signal — they're set as combat winds down to
    // victory/defeat but not when combat is torn down by leaving. Creature-death checks are a fallback.
    private static bool IsCombatResolved(CombatState cs)
    {
        try
        {
            var cm = CombatManager.Instance;
            if (cm != null && (cm.IsOverOrEnding || cm.IsEnding)) return true;
        }
        catch { }
        try
        {
            if (_localSlot >= 0 && _localSlot < _players.Count && _players[_localSlot]?.Creature?.IsDead == true)
                return true; // local player defeated
            bool anyEnemyAlive = false, anyEnemy = false;
            foreach (var e in cs.Enemies) { if (e == null) continue; anyEnemy = true; if (e.IsAlive) { anyEnemyAlive = true; break; } }
            if (anyEnemy && !anyEnemyAlive) return true; // all enemies defeated
        }
        catch { }
        return false;
    }

    private static void OnBlockChanged(Creature creature, int oldBlock, int newBlock)
    {
        if (_disabled) return;
        try
        {
            int gained = newBlock - oldBlock;
            if (gained <= 0 || !creature.IsPlayer) return; // only block *gained*, players only
            int slot = SlotOf(creature.Player);
            if (slot >= 0) _tracker.AddBlock(slot, gained);
        }
        catch { /* overlay must never break combat */ }
    }

    private static void OnHpChanged(Creature creature, int oldHp, int newHp)
    {
        if (_disabled) return;
        try
        {
            int lost = oldHp - newHp;
            if (lost == 0) return;

            int round = SafeCombatState()?.RoundNumber ?? 0;

            if (lost < 0) // HP gained — a heal (tracked as a per-player total, players only)
            {
                if (!creature.IsPlayer) return;
                int hslot = SlotOf(creature.Player);
                if (hslot < 0) return;
                int healed = -lost;
                _tracker.AddHealed(hslot, healed);
                _tracker.AddLog(round, $"{MaybeName(creature)} healed  {healed}", false);
                return;
            }

            _pending.TryGetValue(creature, out var pend);

            if (creature.IsPlayer)
            {
                int slot = SlotOf(creature.Player);
                if (slot < 0) return;
                string attacker = (pend.Dealer != null && pend.Dealer.IsMonster)
                    ? (TextHelper.SafeGetText(() => pend.Dealer!.Monster!.Title) ?? "Enemy")
                    : (pend.Label ?? ConsumeActive().Label ?? "Unknown");
                string victim = MaybeName(creature);
                _tracker.AddTaken(round, slot, lost, attacker);
                _tracker.AddLog(round, $"{attacker} → {victim}  {lost}", true);
                return;
            }

            // Enemy lost HP -> damage dealt by a player (or their pet/summon, e.g. Necrobinder's Osty).
            var dealer = pend.Dealer;
            int dslot;
            if (dealer != null && dealer.IsPlayer) dslot = SlotOf(dealer.Player);
            else if (dealer != null && dealer.IsPet) dslot = SlotOf(dealer.PetOwner); // pet → its owner
            else if (dealer != null && dealer.IsMonster) return; // genuine enemy-on-enemy, not player damage
            else dslot = ActiveSlot();                            // dealer null (poison/doom/etc.) -> local player
            string src; string? icon;
            if (pend.Label != null) { src = pend.Label; icon = pend.Icon; }
            else { var a = ConsumeActive(); src = a.Label ?? "Status"; icon = a.Icon; } // Doom/Kill & other non-Damage paths
            _tracker.AddDealt(round, dslot, lost, src, icon);
            _tracker.AddLog(round, $"{src} → {TextHelper.SafeGetText(() => creature.Monster!.Title) ?? "Enemy"}  {lost}", false);
        }
        catch { /* overlay must never break combat */ }
    }

    private static string MaybeName(Creature c)
    {
        try
        {
            if (c.IsPlayer)
                return _players.Count <= 1 ? "you" : (TextHelper.SafeGetText(() => c.Player!.Character.Title) ?? "you");
            return TextHelper.SafeGetText(() => c.Monster!.Title) ?? "?";
        }
        catch { return "?"; }
    }

    // Read and clear the active power/relic source (set by a patched hook just before it changed HP).
    private static bool ActiveFresh() => _activeSource.HasValue && _frame - _activeFrame <= 1;

    private static (string? Label, string? Icon) ConsumeActive()
    {
        if (ActiveFresh()) { var v = _activeSource!.Value; _activeSource = null; return (v.Label, v.Icon); }
        return (null, null);
    }

    private static int SlotOf(Player? player)
    {
        if (player == null) return -1;
        for (int i = 0; i < _players.Count; i++) if (ReferenceEquals(_players[i], player)) return i;
        return -1;
    }

    private static int ActiveSlot()
    {
        try { var rs = SafeRunState(); if (rs != null) { int s = SlotOf(LocalContext.GetMe(rs)); if (s >= 0) return s; } }
        catch { }
        return _players.Count > 0 ? 0 : -1;
    }

    // ===== Safe accessors =====

    // ===== Hide when a game menu / modal / overlay is on top =====
    private static Type? _tCapstone, _tModal, _tOverlay, _tMap;
    private static bool _blockersResolved;

    private static void ResolveBlockers()
    {
        if (_blockersResolved) return;
        _blockersResolved = true;
        try
        {
            Type[] types;
            try { types = typeof(CombatManager).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
            foreach (var t in types)
            {
                if (t == null) continue;
                switch (t.Name)
                {
                    case "NCapstoneContainer": _tCapstone = t; break; // deck view, settings, etc.
                    case "NModalContainer": _tModal = t; break;        // popups / dialogs
                    case "NOverlayStack": _tOverlay = t; break;        // overlay screens (rewards, card select)
                    case "NMapScreen": _tMap = t; break;               // the map (player has moved on)
                }
            }
        }
        catch { }
    }

    // A full menu/dialog (settings, deck, popup) — overlay should get out of the way.
    private static bool IsMenuOpen()
    {
        try
        {
            ResolveBlockers();
            return InstanceBool(_tCapstone, "InUse")
                || InstanceMemberNotNull(_tModal, "OpenModal", isMethod: false);
        }
        catch { return false; }
    }

    // Menu OR an overlay screen (rewards / card select). Used during combat.
    private static bool IsBlockingUiOpen()
    {
        try { return IsMenuOpen() || InstanceMemberNotNull(_tOverlay, "Peek", isMethod: true); }
        catch { return false; }
    }

    // The map is open → the player has left the post-combat moment.
    private static bool IsMapOpen()
    {
        try { ResolveBlockers(); return InstanceBool(_tMap, "IsOpen") || InstanceBool(_tMap, "Visible"); }
        catch { return false; }
    }

    private static object? GetInstance(Type? t)
        => t?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

    private static bool InstanceBool(Type? t, string prop)
    {
        var i = GetInstance(t);
        return i != null && i.GetType().GetProperty(prop)?.GetValue(i) is bool b && b;
    }

    private static bool InstanceMemberNotNull(Type? t, string member, bool isMethod)
    {
        var i = GetInstance(t);
        if (i == null) return false;
        object? val = isMethod
            ? i.GetType().GetMethod(member, Type.EmptyTypes)?.Invoke(i, null)
            : i.GetType().GetProperty(member)?.GetValue(i);
        return val != null;
    }

    private static bool SafeIsInCombat() { try { return CombatManager.Instance?.IsInProgress == true; } catch { return false; } }
    private static CombatState? SafeCombatState() { try { return CombatManager.Instance?.DebugOnlyGetState(); } catch { return null; } }
    private static RunState? SafeRunState() { try { return RunManager.Instance.IsInProgress ? RunManager.Instance.DebugOnlyGetState() : null; } catch { return null; } }
    private static bool SafeIsRunInProgress() { try { return RunManager.Instance.IsInProgress; } catch { return false; } }

    private static void SafeTeardown()
    {
        try { UnsubscribeAll(); } catch { }
        try { _bars?.Dispose(); } catch { }
        try { _detail?.Dispose(); } catch { }
        try { _tooltip?.Dispose(); } catch { }
        _bars = null; _detail = null; _tooltip = null;
    }
}
