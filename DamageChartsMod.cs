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
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using STS2_DamageCharts.Charts;
using STS2_DamageCharts.Common;
using STS2_DamageCharts.Forecast;
using STS2_DamageCharts.Overlay;
using STS2_DamageCharts.Share;
using STS2_DamageCharts.Stats;
using STS2_DamageCharts.Tracking;
using STS2_DamageCharts.Ui;

namespace STS2_DamageCharts;

// Standalone in-combat damage analytics overlay. Reads game state directly; no dependency on
// any other mod. Capture is event-driven (Creature.CurrentHpChanged) for amounts, with a single
// synchronous Harmony Prefix on CreatureCmd.Damage to attribute each hit to its source (card name
// or, for powers/relics/orbs, the calling type resolved from the stack) and dealer.
[ModInitializer("Initialize")]
public static class DamageChartsMod
{
    private const string ConfigFileName = "STS2_DamageCharts.conf";
    private const string BuildTag = "2026-11-15";  // stamped at init so logs identify the loaded DLL

    // Per-player colors for the current run: each class's own color (as on the game's Statistics
    // screen), with a different shade for a repeated class. Set at combat start.
    private static Color[] _playerColors = UiTheme.Players;

    private static bool _initialized;
    private static bool _disabled;            // hard kill-switch: once set, nothing runs again
    private static Callable _tickCallable;     // stored so we can disconnect ProcessFrame on disable
    private static readonly string[] HarmonyIds =
        { "com.sts2damagecharts", "com.sts2damagecharts.suppress", "com.sts2damagecharts.deck", "com.sts2damagecharts.powers" };
    // The full always-on per-round chart read as "too much" for casual players, so the default is a compact
    // strip docked above the forecast ("bars_style": "full" restores the big one; "show_bars": false hides it).
    private static bool _showBars = true;
    private static bool _barsCompact = true;
    private static bool _barsDocked = true;
    private static bool _showForecast = true;
    private static float? _fcPosXFrac;
    private static float? _fcPosYFrac;
    // Forecast placement: "player" (default) docks it beside the local player's creature, clear of enemy
    // intents; "free" uses the dragged/saved fraction. Dragging the panel switches to free.
    private static bool _fcAnchorPlayer = true;
    private static string _lastForecastLog = "";

    private static readonly DamageTracker _tracker = new();
    private static readonly RunAggregate _run = new();
    private static bool _runActiveLast;
    private static readonly Dictionary<Creature, Action<int, int>> _subs = new();
    private static readonly Dictionary<Creature, Action<int, int>> _blockSubs = new();
    // What's about to hit each creature, set by the Damage prefix (or Doom), consumed by the HP change.
    // Frame-stamped: a tag whose hit never changed HP (fully blocked, or Osty soaked it) expires.
    private static readonly Dictionary<Creature, (string Label, Creature? Dealer, string? Icon, long Frame, string? Id)> _pending = new();
    private const long PendingTtlFrames = 600; // ~10 s at 60 fps: covers Doom's kill animation
    // Damaging powers that sit on an enemy (Poison, Constrict...) hurt that enemy, often after an await
    // (Accelerant makes Poison tick several times with a pause between), when the 1-frame stamp below has
    // gone stale. So the stamp also tags the enemy itself, by reference, the way Doom kills are pre-tagged.
    private static readonly Dictionary<Creature, (string Label, Creature? Owner, string? Icon, string? Id)> _dotTags = new();
    // Set by a power's hook just before it deals damage; consumed by the next non-card hit. Only valid
    // for ~1 frame (a damaging hook stamps-and-deals synchronously) so a stale stamp from a passive
    // relic hook can't mislabel a later unrelated hit (e.g. a potion).
    // The power/relic/orb/potion whose hook is about to change HP, and the player it belongs to.
    // Id is the source model's id ("POWER.POISON_POWER"), for the run stats file; null when unknown.
    private static (string Label, string? Icon, Player? Owner, string? Id)? _activeSource;
    private static long _frame;
    private static long _activeFrame;
    private static string? _doomIcon;
    private static IReadOnlyList<Player> _players = Array.Empty<Player>();
    private static bool _combatLast;
    private static int _errCount;

    private static DamageChartView? _bars;
    private static DamageDetailView? _detail;
    private static DamageTooltipView? _tooltip;
    private static DamageForecastView? _forecast;
    private static ForecastLedgerView? _forecastLedger;
    private static ForecastResult? _frozenForecast;  // last planning-phase value, held during the enemy turn
    private static bool _detailVisible;
    private static bool _summaryActive;       // auto end-of-combat summary showing
    private static bool _summaryAvailable;    // post-combat moment (until the map opens): the hotkey toggles the summary
    private static bool _rewardsSeen;         // the rewards list has been on top during this post-combat moment
    private static bool _summaryOnEnd = true; // config
    private static bool _recapVisible;        // on-demand run recap takeover showing (out of combat)
    private static bool _recapEnabled = true; // config
    private static float _uiScale = 1f;       // config: extra font/spacing multiplier for the takeover
    // Power/relic/orb/potion source-attribution Harmony hooks. Default ON since the curated DamageSourceHooks
    // list (59 methods) replaced the old blanket patching of every hook method (~791), which could intermittently
    // deadlock the CLR's reflection/JIT (hard hang, "Bad IL range"); soak-tested with scripts/soak_source_hooks.py.
    // "source_hooks": false turns it off, and DoT/relic damage is then labeled "Status"/"Other". See docs/KNOWN_ISSUES.md.
    private static bool _sourceHooks = true;
    private static bool _combatResolved;      // this combat reached a win/loss (not abandoned)
    private static FightMeta _fightMeta = new(); // this combat's floor / name / elite-boss / rounds, for the recap
    // Per-run damage stats file (single-player and co-op; "run_stats_file": false turns it off). See RunStatsFile.cs.
    private static bool _runStatsFile = true;
    private static string _modVersion = BuildTag; // run stats "mod_version" (RunStatsFile.ModVersion); never empty
    // Which run this combat belongs to, read at combat start; the fight is written only if the run is the
    // same at combat end, and the recap aggregate is keyed to it, so another run's stats never mix in.
    private sealed record RunIdent(string RunId, string[] SteamIds, string? LocalId);
    private static RunIdent? _fightRun;
    private static RunState? _fightRunState;   // the RunState _fightRun was read from (Share button fallback)
    private static FieldInfo? _startTimeField;
    private static bool _startTimeResolved;
    // Fights written this session on the current floor of the current run: tells a fight replayed after a
    // reload (same floor, same ordinal, replaces the stored one) from a second fight on that floor.
    private static string? _statsRunId;
    private static int _statsFloor;
    private static int _statsFloorCount;
    private static bool _tipShown;            // the hover tooltip was rendered this tick (else it is hidden)
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
        // Applied before the config check: it protects game startup even when the overlay is disabled.
        TryApplySentryShutdownWorkaround();
        try
        {
            if (!LoadConfig()) { GD.Print("[STS2 Damage] disabled via config"); return; }
            _modVersion = RunStatsFile.ModVersion(ReadModVersion(), AssemblyVersion(), BuildTag);

            var tree = (SceneTree)Engine.GetMainLoop();
            _tickCallable = Callable.From(Tick);
            tree.Connect(SceneTree.SignalName.ProcessFrame, _tickCallable);

            TryApplyDamageHook();
            TryApplyPotionHook();
            TryApplySuppressHook();
            if (_sourceHooks) TryApplyPowerSourceHooks();
            else GD.Print("[STS2 Damage] power-source hooks DISABLED via config");
            TryApplyGameOverHook();
            _initialized = true;
            GD.Print($"[STS2 Damage] initialized (build {BuildTag}, source_hooks={_sourceHooks})");
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] init failed: {ex.Message}"); }
    }

    // ===== Sentry startup-freeze workaround =====

    // When any mod is loaded, SentryService.AfterGameInit calls SentryService.Shutdown(), which disposes
    // the .NET SDK and then calls `shutdown` on the native sentry-godot node (SentryInit). The native
    // shutdown leaves its per-frame CFRunLoop observer registered; depending on startup timing that observer
    // touches a freed object and the main thread hangs before the main menu (stack: libsentry ->
    // Object::get_instance_id). Reproduces with any C# mod, manual or Workshop install.
    // Fix: null SentryService._sentryInit just before Shutdown runs, so the game still disposes the .NET SDK
    // and disables itself but skips the native call. The native side keeps the AlwaysRejectEvent sampler the
    // game installs for modded runs (DisableGdExtensionIfModded), so nothing extra is reported.
    // Own Harmony id, deliberately not in HarmonyIds: the overlay's self-disable must not remove it.
    private static FieldInfo? _sentryInitField;

    private static void TryApplySentryShutdownWorkaround()
    {
        try
        {
            var svc = AccessTools.TypeByName("MegaCrit.Sts2.Core.Debug.SentryService");
            var shutdown = svc != null ? AccessTools.DeclaredMethod(svc, "Shutdown") : null;
            _sentryInitField = svc != null ? AccessTools.Field(svc, "_sentryInit") : null;
            if (shutdown == null || _sentryInitField == null || !_sentryInitField.IsStatic)
            {
                GD.Print("[STS2 Damage] sentry shutdown workaround not applied (game changed?)");
                return;
            }
            new Harmony("com.sts2damagecharts.sentry").Patch(shutdown,
                prefix: new HarmonyMethod(typeof(DamageChartsMod).GetMethod(nameof(SentryShutdownPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
            GD.Print("[STS2 Damage] sentry shutdown workaround applied");
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] sentry shutdown workaround skipped: {ex.Message}"); }
    }

    private static void SentryShutdownPrefix()
    {
        try
        {
            if (_sentryInitField?.GetValue(null) == null) return;
            _sentryInitField.SetValue(null, null);
            GD.Print("[STS2 Damage] skipped native Sentry shutdown (modded-startup freeze workaround)");
        }
        catch { /* never block the game's own shutdown path */ }
    }

    // ===== Config =====

    private static string? ConfigPath()
    {
        string? dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        return dir == null ? null : Path.Combine(dir, ConfigFileName);
    }

    // "version" from the mod manifest installed beside the DLL (mods/STS2_DamageCharts.json).
    private static string? ReadModVersion()
    {
        try
        {
            string? dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (dir == null) return null;
            string path = Path.Combine(dir, "STS2_DamageCharts.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch { return null; }
    }

    // The DLL's own version (the csproj stamps mod_manifest.json's "version" into it): informational, else file.
    private static string? AssemblyVersion()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        }
        catch { return null; }
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
                try { File.WriteAllText(path, "{\n  \"enabled\": true,\n  \"hotkey\": \"c\",\n  \"show_bars\": true,\n  \"native_visuals\": true\n}\n"); }
                catch { }
                RememberConfigWriteTime(path);
                return true;
            }

            RememberConfigWriteTime(path);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False) return false;
            if (root.TryGetProperty("show_bars", out var sb) && sb.ValueKind == JsonValueKind.False) _showBars = false;
            _barsCompact = !(root.TryGetProperty("bars_style", out var bst) && bst.ValueKind == JsonValueKind.String && bst.GetString() == "full");
            _barsDocked = !(root.TryGetProperty("bars_anchor", out var ban) && ban.ValueKind == JsonValueKind.String && ban.GetString() == "free");
            if (root.TryGetProperty("forecast", out var fc) && fc.ValueKind == JsonValueKind.False) _showForecast = false;
            if (root.TryGetProperty("forecast_pos_x_frac", out var fpx) && fpx.TryGetSingle(out var ffx)) _fcPosXFrac = ffx;
            if (root.TryGetProperty("forecast_pos_y_frac", out var fpy) && fpy.TryGetSingle(out var ffy)) _fcPosYFrac = ffy;
            _fcAnchorPlayer = !(root.TryGetProperty("forecast_anchor", out var fan) && fan.ValueKind == JsonValueKind.String && fan.GetString() == "free");
            if (root.TryGetProperty("forecast_debug", out var fdb) && fdb.ValueKind == JsonValueKind.True) ForecastReader.Debug = true;
            if (root.TryGetProperty("summary_on_combat_end", out var su) && su.ValueKind == JsonValueKind.False) _summaryOnEnd = false;
            if (root.TryGetProperty("run_recap", out var rr) && rr.ValueKind == JsonValueKind.False) _recapEnabled = false;
            if (root.TryGetProperty("ui_scale", out var us) && us.ValueKind == JsonValueKind.Number && us.TryGetSingle(out float uss)) _uiScale = Math.Clamp(uss, 0.5f, 4f);
            if (root.TryGetProperty("source_hooks", out var shk) && shk.ValueKind == JsonValueKind.False) _sourceHooks = false;
            if (root.TryGetProperty("run_stats_file", out var rsf) && rsf.ValueKind == JsonValueKind.False) _runStatsFile = false;
            if (root.TryGetProperty("native_visuals", out var nv) && nv.ValueKind == JsonValueKind.False) GameAssets.Enabled = false;
            if (root.TryGetProperty("vfx_moments", out var vm) && vm.ValueKind == JsonValueKind.True) VfxMoments.Enabled = true;
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
                ["forecast"] = _showForecast,
                ["forecast_pos_x_frac"] = _fcForecastPosRounded().X,
                ["forecast_pos_y_frac"] = _fcForecastPosRounded().Y,
                ["forecast_anchor"] = _fcAnchorPlayer ? "player" : "free",
                ["bars_style"] = _barsCompact ? "compact" : "full",
                ["bars_anchor"] = _barsDocked ? "forecast" : "free",
                ["native_visuals"] = GameAssets.Enabled,
                ["vfx_moments"] = VfxMoments.Enabled,
            };
            if (ForecastReader.Debug) dict["forecast_debug"] = true;
            // Merge over the existing file so keys this method doesn't own (source_hooks, ui_scale, run_recap,
            // anything added later) survive a drag-save instead of silently resetting to their defaults.
            var merged = new System.Text.Json.Nodes.JsonObject();
            try
            {
                if (File.Exists(path) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) is System.Text.Json.Nodes.JsonObject old)
                    foreach (var kv in old) merged[kv.Key] = kv.Value?.DeepClone();
            }
            catch { /* unreadable file: rewrite from our keys */ }
            foreach (var kv in dict) merged[kv.Key] = JsonSerializer.SerializeToNode(kv.Value);
            File.WriteAllText(path, merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            RememberConfigWriteTime(path);
            _posXFrac = pos.X; _posYFrac = pos.Y;
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] save config failed: {ex.Message}"); }
    }

    private static Vector2 _fcForecastPosRounded()
    {
        var p = _forecast != null ? _forecast.PositionFraction : new Vector2(_fcPosXFrac ?? 0f, _fcPosYFrac ?? 0f);
        return new Vector2((float)Math.Round(p.X, 4), (float)Math.Round(p.Y, 4));
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
            VfxMoments.Enabled = root.TryGetProperty("vfx_moments", out var vm) && vm.ValueKind == JsonValueKind.True;
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
            string prefixName = _damageHookTakesTargetData ? nameof(DamageTargetDataPrefix) : nameof(DamagePrefix);
            var prefix = new HarmonyMethod(typeof(DamageChartsMod).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static));
            new Harmony("com.sts2damagecharts").Patch(method, prefix: prefix);
            GD.Print("[STS2 Damage] damage dealer hook applied");
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] damage hook skipped: {ex.GetType().Name}: {ex.Message}"); }
    }

    // Potions used: postfix on CombatHistory.PotionUsed (sts2.dll v0.107.1), which the game calls once a potion
    // has actually resolved, right before it records a PotionUsedEntry. Plain (non-async) method; args are
    // matched by type, not name: the PotionModel and the optional target Creature.
    private static void TryApplyPotionHook()
    {
        try
        {
            var hist = AccessTools.TypeByName("MegaCrit.Sts2.Core.Combat.History.CombatHistory");
            var method = hist != null ? AccessTools.Method(hist, "PotionUsed") : null;
            if (method == null) { GD.Print("[STS2 Damage] potion hook not resolved; potions won't be listed"); return; }
            var postfix = new HarmonyMethod(typeof(DamageChartsMod).GetMethod(nameof(PotionUsedPostfix), BindingFlags.NonPublic | BindingFlags.Static));
            new Harmony("com.sts2damagecharts").Patch(method, postfix: postfix);
            GD.Print("[STS2 Damage] potion hook applied");
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] potion hook skipped: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void PotionUsedPostfix(object[] __args)
    {
        try
        {
            PotionModel? potion = null;
            Creature? target = null;
            foreach (var a in __args)
            {
                if (a is PotionModel p) potion = p;
                else if (a is Creature c) target = c;
            }
            if (potion == null) return;
            string name = TextHelper.SafeGetText(() => potion.Title) ?? "Potion";
            int slot = SlotOf(potion.Owner);
            if (slot < 0) slot = ActiveSlot();
            string? icon = GameAssets.PotionIconPath(potion);
            if (icon == null) { try { if (potion.ImagePath is string ip && ResourceLoader.Exists(ip)) icon = ip; } catch { } }
            _tracker.AddPotion(slot, name, icon, ModelIdOf(potion));
            int round = SafeCombatState()?.RoundNumber ?? 0;
            string onto = target == null ? "" : target.IsPlayer ? (target.Player == potion.Owner ? "" : $" → {MaybeName(target)}")
                : $" → {TextHelper.SafeGetText(() => target.Monster!.Title) ?? "Enemy"}";
            _tracker.AddLog(round, $"{name} used{onto}", false);
        }
        catch { /* never disrupt the game's potion flow */ }
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
            // Trackpad two-finger scroll arrives as a pan gesture on macOS, not as wheel buttons.
            if (inputEvent is InputEventPanGesture pg && (_detailVisible || _summaryActive || _recapVisible) && _detail != null)
            {
                _detail.ScrollByPan(pg.Delta.Y);
                return false;
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

    // The central overload, which every other Damage overload funnels into: IEnumerable<Creature> targets,
    // a decimal amount, and dealer + cardSource. Pick the widest such overload: v0.107 has
    // (ctx, targets, decimal, ValueProp, dealer, cardSource); v0.111 appends a CardPlay, and a 6-param
    // DamageVar wrapper appears there that must not be mistaken for it.
    private static bool _damageHookTakesTargetData;

    private static bool IsTargetDataList(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>) && t.GetGenericArguments()[0].Name == "DamageTargetData";

    private static MethodBase? ResolveDamageMethod()
    {
        // v0.113 adds the real innermost overload, (ctx, IEnumerable<DamageTargetData> targetData, decimal, ValueProp,
        // dealer, cardSource, CardPlay, ModifyDamageHookType): the IEnumerable<Creature> one wraps each target in a
        // DamageTargetData and calls it, but attacks with per-target hit VFX call it directly, so only it sees every hit.
        MethodInfo? best = null;
        bool bestIsTargetData = false;
        foreach (var m in typeof(MegaCrit.Sts2.Core.Commands.CreatureCmd).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "Damage") continue;
            var ps = m.GetParameters();
            if (ps.Length < 2) continue;
            bool targetData = IsTargetDataList(ps[1].ParameterType);
            if (!targetData && ps[1].ParameterType != typeof(IEnumerable<Creature>)) continue;
            if (!ps.Any(p => p.ParameterType == typeof(decimal))) continue;
            if (!ps.Any(p => p.Name == "dealer") || !ps.Any(p => p.Name == "cardSource")) continue;
            if (best == null || (targetData && !bestIsTargetData)
                || (targetData == bestIsTargetData && ps.Length > best.GetParameters().Length))
            {
                best = m;
                bestIsTargetData = targetData;
            }
        }
        _damageHookTakesTargetData = bestIsTargetData;
        if (best != null)
            GD.Print($"[STS2 Damage] damage hook: CreatureCmd.Damage({string.Join(", ", best.GetParameters().Select(p => p.ParameterType.Name))})");
        return best;
    }

    // The model methods that can change a creature's HP in combat, i.e. the only hooks worth stamping.
    // Generated by a static call-graph sweep of sts2.dll v0.107.1 (REA inspect-managed-members over every
    // Powers/Relics/Orbs/Potions type, following async state machines and same-type helpers to
    // CreatureCmd.Damage/Kill/SetCurrentHp/LoseMaxHp). Pickup-time AfterObtained hooks and Fur Coat's
    // SetCurrentHp are left out. Re-run the sweep after game updates; missing names are skipped and logged.
    // The list is the union of v0.107.1 and v0.111.0 (beta): v0.111 adds Cacophony and Doom's
    // AfterSideTurnEnd and removes OutbreakPower (Outbreak became a card that triggers Poison directly).
    // Patching only these (instead of every hook override, ~791 methods) avoids the metadata corruption /
    // CLR deadlock described in docs/KNOWN_ISSUES.md.
    private static readonly (string Type, string[] Methods)[] DamageSourceHooks =
    {
        ("Orbs.DarkOrb", new[] { "Evoke" }),
        ("Orbs.GlassOrb", new[] { "Evoke", "Passive" }),
        ("Orbs.LightningOrb", new[] { "ApplyLightningDamage", "Evoke", "Passive" }),
        ("Potions.ExplosiveAmpoule", new[] { "OnUse" }),
        ("Potions.FirePotion", new[] { "OnUse" }),
        ("Potions.FoulPotion", new[] { "OnUse" }),
        ("Potions.PotionShapedRock", new[] { "OnUse" }),
        ("Powers.BlackHolePower", new[] { "AfterCardPlayed", "AfterStarsGained", "DealDamageToAllEnemies" }),
        ("Powers.CacophonyPower", new[] { "AfterCardDrawn" }),
        ("Powers.ConstrictPower", new[] { "AfterSideTurnEnd" }),
        ("Powers.CrimsonMantlePower", new[] { "AfterPlayerTurnStart" }),
        ("Powers.DemisePower", new[] { "AfterSideTurnEnd" }),
        ("Powers.DisintegrationPower", new[] { "AfterSideTurnEndLate" }),
        ("Powers.DoomPower", new[] { "AfterSideTurnEnd", "BeforeSideTurnEnd" }),
        ("Powers.FlameBarrierPower", new[] { "AfterDamageReceived" }),
        ("Powers.GalvanicPower", new[] { "AfterCardPlayed" }),
        ("Powers.GravityPower", new[] { "AfterCardPlayed" }),
        ("Powers.HailstormPower", new[] { "BeforeSideTurnEnd" }),
        ("Powers.HauntPower", new[] { "AfterCardPlayed" }),
        ("Powers.InfernoPower", new[] { "AfterDamageReceived", "AfterPlayerTurnStart" }),
        ("Powers.JuggernautPower", new[] { "AfterBlockGained" }),
        ("Powers.MagicBombPower", new[] { "AfterSideTurnEnd" }),
        ("Powers.NecroMasteryPower", new[] { "AfterCurrentHpChanged" }),
        ("Powers.OutbreakPower", new[] { "AfterPowerAmountChanged" }),
        ("Powers.PanachePower", new[] { "AfterCardPlayed" }),
        ("Powers.PaperCutsPower", new[] { "AfterDamageGiven" }),
        ("Powers.PoisonPower", new[] { "AfterSideTurnStart" }),
        ("Powers.ReflectPower", new[] { "AfterDamageReceived" }),
        ("Powers.RollingBoulderPower", new[] { "AfterPlayerTurnStart", "DoDamage" }),
        ("Powers.SandpitPower", new[] { "AfterRemoved" }),
        ("Powers.SerpentFormPower", new[] { "AfterCardPlayed" }),
        ("Powers.SleightOfFleshPower", new[] { "AfterPowerAmountChanged" }),
        ("Powers.SmokestackPower", new[] { "AfterCardGeneratedForCombat" }),
        ("Powers.SpeedsterPower", new[] { "AfterCardDrawn" }),
        ("Powers.StranglePower", new[] { "AfterCardPlayed" }),
        ("Powers.TheBombPower", new[] { "BeforeSideTurnEnd" }),
        ("Powers.TheGambitPower", new[] { "AfterDamageReceived" }),
        ("Powers.ThornsPower", new[] { "BeforeDamageReceived" }),
        ("Powers.ThunderPower", new[] { "AfterOrbEvoked" }),
        ("Relics.CharonsAshes", new[] { "AfterCardExhausted" }),
        ("Relics.FestivePopper", new[] { "AfterPlayerTurnStart" }),
        ("Relics.ForgottenSoul", new[] { "AfterCardExhausted" }),
        ("Relics.Kusarigama", new[] { "AfterCardPlayed" }),
        ("Relics.LetterOpener", new[] { "AfterCardPlayed" }),
        ("Relics.LostWisp", new[] { "AfterCardPlayed" }),
        ("Relics.MercuryHourglass", new[] { "AfterPlayerTurnStart" }),
        ("Relics.Metronome", new[] { "AfterOrbChanneled" }),
        ("Relics.MrStruggles", new[] { "AfterPlayerTurnStart" }),
        ("Relics.ParryingShield", new[] { "AfterSideTurnEnd" }),
        ("Relics.RoyalPoison", new[] { "AfterPlayerTurnStart" }),
        ("Relics.ScreamingFlagon", new[] { "BeforeSideTurnEnd" }),
        ("Relics.StoneCalendar", new[] { "BeforeSideTurnEnd" }),
        ("Relics.Tingsha", new[] { "AfterCardDiscarded" }),
    };

    // Patch the curated damage-dealing hooks (DamageSourceHooks). The prefix reads the model's own
    // Title/icon from __instance — no stack walking — and stashes it as the active source for the Damage
    // call that immediately follows.
    private static void TryApplyPowerSourceHooks()
    {
        try
        {
            var prefix = new HarmonyMethod(typeof(DamageChartsMod).GetMethod(nameof(PowerSourcePrefix), BindingFlags.NonPublic | BindingFlags.Static));
            var doomPrefix = new HarmonyMethod(typeof(DamageChartsMod).GetMethod(nameof(DoomKillPrefix), BindingFlags.NonPublic | BindingFlags.Static));
            try { var dp = "res://images/atlases/power_atlas.sprites/doom_power.tres"; if (ResourceLoader.Exists(dp)) _doomIcon = dp; } catch { }
            var h = new Harmony("com.sts2damagecharts.powers");

            // Doom kills via CreatureCmd.Kill (animation-delayed) — pre-tag the doomed creatures by
            // reference so the kill-time HP loss is labeled "Doom" regardless of timing.
            var doom = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Powers.DoomPower");
            var dk = doom != null ? AccessTools.DeclaredMethod(doom, "DoomKill") : null;
            if (dk != null) { try { h.Patch(dk, prefix: doomPrefix); } catch { } }

            int n = 0;
            var missing = new List<string>();
            foreach (var (typeName, methods) in DamageSourceHooks)
            {
                var t = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models." + typeName);
                if (t == null) { missing.Add(typeName); continue; }
                foreach (var name in methods)
                {
                    bool found = false;
                    foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.Name != name || m.IsAbstract || m.IsGenericMethod) continue;
                        found = true;
                        try { h.Patch(m, prefix: prefix); n++; }
                        catch { }
                    }
                    if (!found) missing.Add(typeName + "." + name);
                }
            }
            GD.Print($"[STS2 Damage] power-source hooks applied to {n} methods");
            if (missing.Count > 0) GD.Print($"[STS2 Damage] power-source hooks not found (game changed?): {string.Join(", ", missing)}");
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] power-source hooks skipped: {ex.Message}"); }
    }

    // Pre-tag doomed creatures so their kill-time HP loss is attributed to "Doom".
    private static void DoomKillPrefix(System.Collections.Generic.IReadOnlyList<Creature> creatures)
    {
        try
        {
            if (creatures == null) return;
            // Credit the kill to whoever applied the Doom (in co-op it isn't always the local player).
            foreach (var c in creatures)
                if (c != null) _pending[c] = ("Doom", DoomApplier(c)?.Creature, _doomIcon, _frame, null);
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
            var owner = SourcePlayer(__instance);
            string? id = ModelIdOf(__instance);
            _activeSource = (label!, icon, owner, id);
            _activeFrame = _frame;
            if (__instance is PowerModel power && power.Owner is { } holder && !holder.IsPlayer && !holder.IsPet)
                _dotTags[holder] = (label!, owner?.Creature, icon, id);
            if (ForecastReader.Debug) GD.Print($"[STS2 Damage] STAMP {t.Name} label={label} frame={_frame}");
        }
        catch { }
    }

    // v0.113's innermost overload. targetData is IEnumerable<DamageTargetData> (a record struct with a public
    // `target` field); typed as object so this mod still loads on versions without that type.
    private static void DamageTargetDataPrefix(object targetData, Creature dealer, CardModel cardSource)
    {
        try
        {
            if (targetData is not System.Collections.IEnumerable items) return;
            var targets = new List<Creature>();
            foreach (var d in items)
            {
                if (d == null) continue;
                _targetField ??= d.GetType().GetField("target");
                if (_targetField == null) { ErrorLog.NoteOnce($"{d.GetType().Name}.target not found (game changed?): damage not attributed"); return; }
                if (_targetField.GetValue(d) is Creature c) targets.Add(c);
            }
            DamagePrefix(targets, dealer, cardSource);
        }
        catch { /* never disrupt damage resolution */ }
    }

    private static FieldInfo? _targetField;

    private static void DamagePrefix(IEnumerable<Creature> targets, Creature dealer, CardModel cardSource)
    {
        try
        {
            if (targets == null) return;
            string label;
            string? icon = null;
            string? id = null;
            if (cardSource != null)
            {
                label = TextHelper.SafeGetText(() => cardSource.Title) ?? "Card";
                id = ModelIdOf(cardSource);
                icon = GameAssets.CardPortraitPath(cardSource);
            }
            else if (dealer != null && dealer.IsPet)
            {
                // Pet/summon attack (e.g. Osty) — credit the pet by name (the commanding card isn't on this call).
                label = TextHelper.SafeGetText(() => dealer.Monster!.Title) ?? "Pet";
                id = ModelIdOf(InstanceGet(dealer, "Monster"));
            }
            else if (ActiveFresh())
            {
                // A power's hook tagged this damage just before calling Damage (e.g. Poison). Only used
                // when fresh (~same frame) so a stale passive-relic stamp can't mislabel this hit.
                label = _activeSource!.Value.Label;
                icon = _activeSource.Value.Icon;
                id = _activeSource.Value.Id;
                // No dealer on the hit (Poison ticks, relic/orb procs): credit the source's player.
                if (dealer == null) dealer = _activeSource.Value.Owner?.Creature!;
                _activeSource = null; // consume
            }
            else label = "Status"; // unattributed non-card damage
            bool unattributed = label == "Status" && dealer == null;
            var list = targets as IReadOnlyCollection<Creature> ?? targets.ToList();
            foreach (var t in list)
            {
                if (t == null) continue;
                // A stale stamp: a DoT on this enemy hurting it after an await (later Poison ticks).
                if (unattributed && _dotTags.TryGetValue(t, out var dot)) _pending[t] = (dot.Label, dot.Owner, dot.Icon, _frame, dot.Id);
                else _pending[t] = (label, dealer, icon, _frame, id);
                // Damage that reaches a pet (Osty) can spill over to its owner without another Damage call,
                // so tag the owner with the same hit.
                try { if (t.IsPet && t.PetOwner?.Creature is { } po && !list.Contains(po)) _pending[po] = _pending[t]; } catch { }
                VfxMoments.NoteAttack(t, dealer);
            }
        }
        catch { /* never disrupt damage resolution */ }
    }

    // ===== Share button (victory and death screens) =====

    // Postfix on NGameOverScreen._Ready records the screen and picks its run id right away, while the finished
    // run is still loaded (ShareRun.Pick says when an id can be trusted). ShareButton is created lazily and
    // driven from Tick while that screen is on top. No trustworthy id means no button: sharing the wrong run
    // would be worse than not offering it.
    private static ShareButton? _share;
    private static Node? _gameOverScreen;
    private static string? _shareRunId;
    private static bool _shareRunIdNew;          // a new screen: reset the button before showing it
    private static bool _shareIdMissLogged;
    private static Type? _clickableType;

    private static void TryApplyGameOverHook()
    {
        try
        {
            var screen = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NGameOverScreen");
            var ready = screen != null ? AccessTools.DeclaredMethod(screen, "_Ready") : null;
            if (ready == null) { GD.Print("[STS2 Damage] game-over hook not resolved (game changed?): no Share button"); return; }
            var postfix = new HarmonyMethod(typeof(DamageChartsMod).GetMethod(nameof(GameOverReadyPostfix), BindingFlags.NonPublic | BindingFlags.Static));
            new Harmony("com.sts2damagecharts").Patch(ready, postfix: postfix);
            GD.Print("[STS2 Damage] game-over hook applied (Share button)");
            TryApplyShareClickGuard();
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] game-over hook skipped: {ex.GetType().Name}: {ex.Message}"); }
    }

    // The game's buttons (NClickableControl) react to the mouse on their own, not only through GUI input that
    // our MouseFilter.Stop controls could absorb, and our clicks are polled after input is dispatched (too late
    // for SetInputAsHandled). So while the cursor is over the Share button or its panel, skip the game's
    // HandleMousePress/HandleMouseRelease: nothing under it (Continue, View Run, Main Menu, Leaderboard) reacts.
    // Only void or Task methods are patched (a skipped Task method returns a completed task); anything else is
    // left alone and logged.
    private static void TryApplyShareClickGuard()
    {
        try
        {
            _clickableType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl");
            if (_clickableType == null) { GD.Print("[STS2 Damage] share click guard not resolved: clicks on Share may reach the screen"); return; }
            var h = new Harmony("com.sts2damagecharts");
            int patched = 0;
            foreach (var m in _clickableType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m.Name != "HandleMousePress" && m.Name != "HandleMouseRelease") continue;
                string? prefix = m.ReturnType == typeof(void) ? nameof(ShareClickGuardPrefix)
                    : m.ReturnType == typeof(System.Threading.Tasks.Task) ? nameof(ShareClickGuardTaskPrefix) : null;
                if (prefix == null) { GD.Print($"[STS2 Damage] share click guard: {m.Name} returns {m.ReturnType.Name}, not patched"); continue; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(DamageChartsMod).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)));
                patched++;
            }
            if (patched == 0) GD.Print("[STS2 Damage] share click guard not applied (no HandleMousePress/Release found): clicks on Share may reach the screen");
            else GD.Print($"[STS2 Damage] share click guard applied to {patched} method(s)");
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] share click guard skipped: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static bool ShareCoversMouse()
    {
        try { return _share != null && _share.IsValid() && _share.Covers(RootMouse()); }
        catch { return false; }
    }

    private static bool ShareClickGuardPrefix() => !ShareCoversMouse();

    private static bool ShareClickGuardTaskPrefix(ref System.Threading.Tasks.Task __result)
    {
        if (!ShareCoversMouse()) return true;
        __result = System.Threading.Tasks.Task.CompletedTask;
        return false;
    }

    private static void GameOverReadyPostfix(object __instance)
    {
        try
        {
            if (__instance is not Node screen) return;
            _gameOverScreen = screen;
            _shareRunId = null;
            _shareRunIdNew = true;
            bool inProgress = SafeIsRunInProgress();
            var live = inProgress ? SafeRunState() : null;
            var screenRun = RunStateOf(screen);
            bool screenIsLive = live != null && ReferenceEquals(screenRun, live);
            bool fightIsLive = live != null && ReferenceEquals(_fightRunState, live);
            var (id, why) = ShareRun.Pick(inProgress, screenIsLive,
                screenIsLive ? ReadRunIdentity(live)?.RunId : null, fightIsLive, _fightRun?.RunId);
            _shareRunId = id;
            if (id == null)
            {
                if (!_shareIdMissLogged)
                    GD.Print($"[STS2 Damage] game-over screen: no trustworthy run id ({why}, run in progress={inProgress}, " +
                             $"screen run found={screenRun != null}): no Share button");
                _shareIdMissLogged = true;
            }
            else GD.Print($"[STS2 Damage] game-over screen: Share run {id} ({why})");
        }
        catch (Exception ex) { _shareRunId = null; ErrorLog.Once("game-over hook", ex); }
    }

    private static RunState? RunStateOf(object screen)
    {
        try
        {
            foreach (var f in screen.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (typeof(RunState).IsAssignableFrom(f.FieldType) && f.GetValue(screen) is RunState rs) return rs;
        }
        catch { }
        return null;
    }

    // Each frame: show the button while the game-over screen is up and nothing covers it (a menu, another
    // overlay such as run history, or the mod's own run recap). Never counts toward the tick's error limit.
    private static void UpdateShare()
    {
        try
        {
            var screen = _gameOverScreen;
            if (screen == null || !GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree())
            {
                _gameOverScreen = null;
                _share?.Hide();
                return;
            }
            string? top = TopOverlayName();
            bool shown = _shareRunId != null && !_recapVisible && !IsMenuOpen()
                && (top == null || top == "NGameOverScreen")
                && (screen is not CanvasItem ci || ci.IsVisibleInTree());
            if (!shown) { _share?.Hide(); return; }
            if (_share == null || !_share.IsValid())
            {
                _share?.Dispose();
                _share = new ShareButton(((SceneTree)Engine.GetMainLoop()).Root);
                _shareRunIdNew = true;
            }
            if (_shareRunIdNew) { _share.Reset(_shareRunId!, screen); _shareRunIdNew = false; }
            _share.Update(Input.IsMouseButtonPressed(MouseButton.Left));
        }
        catch (Exception ex) { try { _share?.Hide(); } catch { } ErrorLog.Once("share", ex); }
    }

    // ===== Per-frame tick =====

    private static void Tick()
    {
        if (_disabled || !_initialized) return;
        long perfTick = Perf.Start();
        try
        {
            _frame++;
            _tipShown = false;
            if (_frame % 30 == 0) ReloadConfigIfChangedOnDisk();
            if (_frame > 120 && _frame % 30 == 0)
            {
                try { UiTheme.EnsurePanelStyle(((SceneTree)Engine.GetMainLoop()).Root); } catch { }
            }
            HandleHotkey();

            // Run lifecycle: a fresh run (not-in-progress -> in-progress) clears the run-wide aggregate
            // so the recap never shows a previous run's data.
            bool runActive = SafeIsRunInProgress();
            if (runActive && !_runActiveLast) { _run.Clear(); _recapVisible = false; _fightRun = null; _fightRunState = null; }
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
                    try { if (cs.RoundNumber > _fightMeta.Rounds) _fightMeta.Rounds = cs.RoundNumber; } catch { }
                    bool blocking = IsBlockingUiOpen();
                    VfxMoments.Tick(cs.RoundNumber, blocking || _detailVisible);
                    if (blocking)
                    {
                        // A game menu/modal/overlay is on top — get out of its way.
                        _bars?.Hide(); _tooltip?.Hide(); _detail?.SetVisible(false); _forecast?.Hide(); _forecastLedger?.Hide();
                    }
                    else if (_detailVisible && _detail != null)
                    {
                        // Full-screen breakdown is up: it takes over; hide the compact bars. The tooltip now
                        // belongs to the takeover's per-round chart.
                        _bars?.Hide(); _forecast?.Hide(); _forecastLedger?.Hide();
                        _detail.SummaryMode = false; _detail.RecapMode = false; _detail.UiScaleMul = _uiScale;
                        _detail.ShowRunHistory = true;
                        _detail.RunFights = _run.HasData() ? _run.EncounterStats(_localSlot) : null;
                        _detail.SetVisible(true);
                        _detail.Render(_tracker.SourceSnapshot(), _tracker.Snapshot(), _playerColors);
                        _detail.UpdateMouse(Input.IsMouseButtonPressed(MouseButton.Left));
                        if (_detail.TakeCloseRequest()) { _detailVisible = false; _detail.SetVisible(false); } // ✕ clicked
                        else ShowDetailChartTip(static (chart, row) =>
                            GroupDetails.ForRound(row, chart.Labels, chart.PlayerCount, null, showPct: true, maxRows: 5));

                    }
                    else if (IsFightEndingNow())
                    {
                        // The fight is genuinely winding down to victory/defeat — drop the always-on HUD.
                        // Uses a LIVE check (game ending flags / player dead), not the sticky _combatResolved:
                        // splitting enemies (Phrog -> wrigglers) momentarily clear the board, and a sticky
                        // latch there would hide the HUD for the rest of the fight.
                        _bars?.Hide(); _tooltip?.Hide(); _detail?.SetVisible(false); _forecast?.Hide(); _forecastLedger?.Hide();
                    }
                    else
                    {
                        _detail?.SetVisible(false);
                        // Forecast first: the compact chart docks against the rect it just drew.
                        if (_showForecast) UpdateForecast(cs);
                        if (_showBars && _bars != null)
                        {
                            _bars.UiScaleMul = _uiScale;
                            _bars.Compact = _barsCompact;
                            _bars.Docked = _barsCompact && _barsDocked;
                            _bars.DockRect = _showForecast ? _forecast?.PanelRect : null;
                            long pbar = Perf.Start();
                            _bars.Render(_tracker.Snapshot(), _playerColors);
                            Perf.Stop("bars", pbar);
                            // Poll-based drag + click + hover (no input overrides).
                            if (_bars.UpdateMouse(Input.IsMouseButtonPressed(MouseButton.Left)))
                            {
                                if (_barsCompact) _barsDocked = _bars.Docked;   // a drag undocks it
                                SaveConfig();
                            }
                            if (_bars.TakeClick()) { _detailVisible = !_detailVisible; _detail?.SetVisible(_detailVisible); } // click chart → toggle details
                            if (_tooltip != null) _tooltip.UiScaleMul = _uiScale;
                            if (!_bars.IsDragging && _bars.TryGetHover(out var hv) && _tooltip != null)
                            {
                                _tooltip.Render(hv, _playerColors, RootMouse());
                                _tipShown = true;
                            }
                        }
                        // Its CanvasLayer starts visible; never rendered, its title would sit alone at (0,0).
                        else _bars?.Hide();
                    }
                }
            }
            else if (_recapVisible && _recapEnabled && _run.HasData() && _detail != null)
            {
                // On-demand run recap: a full-screen takeover shown out of combat (e.g. over the run's
                // victory/death screen). Unlike the in-combat takeover this is NOT gated by blocking UI,
                // so it can appear on top of the game's end screen.
                _bars?.Hide(); _forecast?.Hide(); _forecastLedger?.Hide(); _summaryActive = false;
                _detail.SummaryMode = false; _detail.RecapMode = true; _detail.ShowRunHistory = false; _detail.UiScaleMul = _uiScale; _detail.SetVisible(true);
                _detail.Render(_run.RunSourceSnapshot(), _run.RunChartSnapshot(), _playerColors);
                _detail.UpdateMouse(Input.IsMouseButtonPressed(MouseButton.Left));
                if (_detail.TakeCloseRequest()) { _recapVisible = false; _detail.RecapMode = false; _detail.SetVisible(false); }
                else ShowDetailChartTip(static (_, row) => _run.FightDetail(row.Round)); // recap rows: Round = fight number
            }
            else if (_summaryActive && _detail != null)
            {
                // End-of-combat summary: shown beside the rewards list (click-through), hidden while a
                // menu is up, dismissed once the player moves on to the map (or via Cmd+D / next fight).
                _bars?.Hide(); _tooltip?.Hide(); _forecast?.Hide(); _forecastLedger?.Hide();
                _detail.RecapMode = false; _detail.ShowRunHistory = false;
                string? top = TopOverlayName();
                if (IsMapOpen()) { _summaryActive = false; _summaryAvailable = false; _detail.SetVisible(false); }
                else if (IsMenuOpen() || top != "NRewardsScreen")
                {
                    // The summary belongs to the rewards list only. Something over it (a card choice) hides it
                    // for now; an empty stack after the list was shown means the player proceeded, which ends
                    // the moment. Never shown where no rewards list appears (e.g. after an act boss).
                    _detail.SetVisible(false);
                    if (top == null && _rewardsSeen) { _summaryActive = false; _summaryAvailable = false; }
                }
                else
                {
                    _rewardsSeen = true;
                    _detail.SummaryMode = true; _detail.SetVisible(true);
                    _detail.Render(_tracker.SourceSnapshot(), _tracker.Snapshot(), _playerColors);
                    _detail.UpdateMouse(Input.IsMouseButtonPressed(MouseButton.Left));
                    if (_detail.TakeCloseRequest()) { _summaryActive = false; _detail.SetVisible(false); } // ✕ clicked
                    else ShowDetailChartTip(static (_, _) => null);   // summary has no chart: just the ✕ tip
                }
            }

            if (!_tipShown) _tooltip?.Hide();
            UpdateShare();
            _errCount = 0;
        }
        catch (Exception ex)
        {
            try { _tooltip?.Hide(); } catch { }
            // Disable fast on repeated errors so we never spam the broken logger or stall the game.
            if (++_errCount >= 5) HardDisable($"repeated errors: {ex.Message}");
        }
        Perf.Stop("tick", perfTick);
        Perf.EndTick();
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

    // Viewport rect of the local player's creature hitbox: NCombatRoom.Instance.GetCreatureNode(creature).Hitbox
    // (verified in sts2.dll v0.107.1). The reflection lookup runs at most every ~30 frames and only until the
    // node is found; after that we read the cached Control each frame through plain Godot calls.
    private static PropertyInfo? _roomInstanceProp, _hitboxProp;
    private static MethodInfo? _getCreatureNode;
    private static bool _anchorResolved;
    private static Control? _anchorHitbox;
    private static Creature? _anchorCreature;
    private static long _anchorLookupFrame = -1000;

    private static Rect2? LocalPlayerHitboxRect()
    {
        try
        {
            Creature? me = (_localSlot >= 0 && _localSlot < _players.Count) ? _players[_localSlot]?.Creature : null;
            if (me == null) return null;
            if (_anchorHitbox == null || !GodotObject.IsInstanceValid(_anchorHitbox) || !ReferenceEquals(_anchorCreature, me))
            {
                _anchorHitbox = null;
                if (_frame - _anchorLookupFrame < 30) return null;
                _anchorLookupFrame = _frame;
                if (!_anchorResolved)
                {
                    _anchorResolved = true;
                    var room = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom");
                    _roomInstanceProp = room != null ? AccessTools.Property(room, "Instance") : null;
                    _getCreatureNode = room != null ? AccessTools.Method(room, "GetCreatureNode", new[] { typeof(Creature) }) : null;
                    var node = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NCreature");
                    _hitboxProp = node != null ? AccessTools.Property(node, "Hitbox") : null;
                }
                var inst = _roomInstanceProp?.GetValue(null);
                if (inst == null || _getCreatureNode == null || _hitboxProp == null) return null;
                var creatureNode = _getCreatureNode.Invoke(inst, new object[] { me });
                if (creatureNode == null || _hitboxProp.GetValue(creatureNode) is not Control hb) return null;
                _anchorHitbox = hb; _anchorCreature = me;
            }
            var xf = _anchorHitbox.GetGlobalTransformWithCanvas();
            return new Rect2(xf.Origin, _anchorHitbox.Size * xf.Scale);
        }
        catch { _anchorHitbox = null; return null; }
    }

    private static void UpdateForecast(CombatState cs)
    {
        try
        {
            if (_forecast == null) return;
            long pb = Perf.Start();
            var inputs = ForecastReader.Build(cs, _players, _localSlot);
            Perf.Stop("fc.build", pb);
            long pr = Perf.Start();
            var r = DamageForecast.Compute(inputs);
            // Only recompute while the player side is acting (see IsPlanningPhase). During the enemy turn the
            // prediction is being realized — block/Osty get eaten hit by hit — so freeze the last planning
            // value instead of letting the shown loss climb as the animation resolves.
            if (ForecastReader.IsPlanningPhase(cs, _players, _localSlot) || _frozenForecast == null) _frozenForecast = r;
            r = _frozenForecast.Value;
            _forecast.UiScaleMul = _uiScale;
            if (_forecastLedger != null) _forecastLedger.UiScaleMul = _uiScale;
            _forecast.AnchorToPlayer = _fcAnchorPlayer;
            _forecast.AnchorRect = _fcAnchorPlayer ? LocalPlayerHitboxRect() : null;
            _forecast.Render(r);
            if (_forecast.UpdateMouse(Input.IsMouseButtonPressed(MouseButton.Left)))
            {
                _fcAnchorPlayer = _forecast.AnchorToPlayer;   // a drag undocks it
                SaveConfig();
            }
            if (!_forecast.IsDragging && _forecast.IsHovered() && _forecast.LastResult is { } last)
                _forecastLedger?.Render(last, _tracker.Snapshot(), _localSlot, cs.RoundNumber, RootMouse());
            else _forecastLedger?.Hide();
            Perf.Stop("fc.render", pr);
            if (ForecastReader.Debug)
            {
                string detail = "";
                foreach (var it in r.Items) detail += $" [{it.Kind} {it.Label} {it.Hits}x{it.DamagePerHit}]";
                string line = $"in={r.Incoming} block={r.Block} loss={r.MinLoss}-{r.MaxLoss} " +
                              $"hidden={r.HasHidden} hp={r.CurrentHp}/{r.MaxHp} frozen={!ForecastReader.IsPlanningPhase(cs, _players, _localSlot)} items={r.Items.Count}{detail}";
                if (line != _lastForecastLog) { _lastForecastLog = line; GD.Print($"[STS2 Damage] forecast {line}"); }
            }
        }
        catch (Exception ex) { ErrorLog.Once("forecast", ex); }
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
            else if (_summaryAvailable && !IsMapOpen() && TopOverlayName() == "NRewardsScreen")
            {
                // Still on the rewards moment: the hotkey toggles the compact summary (the recap is for later).
                _summaryActive = !_summaryActive;
                if (!_summaryActive) _detail?.SetVisible(false);
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

    // "Close (C)" hover tip for the detail view's close/back button, naming the configured toggle key.
    // Cached per key so the tooltip only lays out again when the key changes.
    private static GroupDetail? _closeTip;
    private static GroupDetail CloseTip(string hotkey)
    {
        string title = $"Close ({hotkey})";
        if (_closeTip == null || _closeTip.Title != title) _closeTip = new GroupDetail { Title = title, MaxRows = 0 };
        return _closeTip;
    }

    // Hover tooltip for the close/back button, else for the full-screen view's chart (per round in combat, per fight in the recap). The
    // detail is built only when the hovered group changes; a failure just leaves the tooltip hidden.
    private static void ShowDetailChartTip(Func<ChartSnapshot, ChartRow, GroupDetail?> build)
    {
        if (_detail == null || _tooltip == null || !_detail.Visible) return;
        try
        {
            var gd = _detail.CloseHovered ? CloseTip(_detail.HotkeyHint) : _detail.HoveredDetail(build);
            if (gd == null) return;
            _tooltip.UiScaleMul = _uiScale;
            _tooltip.Render(gd, _playerColors, RootMouse());
            _tipShown = true;
        }
        catch (Exception ex) { ErrorLog.Once("detail tooltip", ex); }
    }

    // What this combat is, read best-effort from the run state at combat start: run-wide floor, the
    // encounter's title (else the enemies' names) and whether it is an elite or boss fight. Every read is
    // independent and optional; anything missing stays unknown and the tooltip shows less.
    private static FightMeta CaptureFightMeta(RunState? rs, CombatState? cs)
    {
        var m = new FightMeta();
        if (rs == null) return m;
        try { if (InstanceGet(rs, "TotalFloor") is int f && f > 0) m.Floor = f; } catch { }
        try
        {
            object? room = InstanceGet(rs, "CurrentRoom");
            object? enc = InstanceGet(room, "Encounter");
            // The encounter's own type first: a fight started from an event room is still a Monster fight.
            m.Kind = FightMeta.KindFromRoomType(InstanceGet(enc, "RoomType")?.ToString());
            if (m.Kind == FightKind.Unknown) m.Kind = FightMeta.KindFromRoomType(InstanceGet(room, "RoomType")?.ToString());
            if (m.Kind == FightKind.Unknown)
                m.Kind = FightMeta.KindFromRoomType(InstanceGet(InstanceGet(rs, "CurrentMapPoint"), "PointType")?.ToString());
            string? name = enc != null ? TextHelper.SafeGetText(() => InstanceGet(enc, "Title")) : null;
            if (!GroupDetails.LooksLikeLocKey(name)) m.Name = name!.Trim();
            // An event's fight: the combat room names its parent event, or the base room is the event room.
            m.FromEvent = InstanceGet(room, "ParentEventId") != null
                || InstanceGet(InstanceGet(rs, "BaseRoom"), "RoomType")?.ToString() == "Event";
        }
        catch { }
        if (m.Name == null && cs != null)
        {
            try
            {
                var names = new List<string?>();
                foreach (var e in cs.Enemies) if (e != null) names.Add(TextHelper.SafeGetText(() => e.Monster!.Title));
                m.Name = GroupDetails.JoinNames(names);
            }
            catch { }
        }
        return m;
    }

    // ===== Combat lifecycle =====

    private static void OnCombatStart()
    {
        _fightMeta = new FightMeta();
        var rs = SafeRunState();
        _players = rs?.Players ?? (IReadOnlyList<Player>)Array.Empty<Player>();
        _fightRun = null;
        _fightRunState = null;
        if (_players.Count == 0) return;
        _fightRun = ReadRunIdentity(rs);
        _fightRunState = _fightRun != null ? rs : null;
        if (_run.EnsureRun(_fightRun?.RunId)) GD.Print("[STS2 Damage] another run: run recap reset");

        var labels = new string[_players.Count];
        Player? me = rs != null ? LocalContext.GetMe(rs) : null;
        int localSlot = 0;
        _playerColors = PlayerColors(_players);
        for (int i = 0; i < _players.Count; i++)
        {
            string name = TextHelper.SafeGetText(() => _players[i].Character.Title) ?? $"P{i + 1}";
            bool isMe = ReferenceEquals(_players[i], me);
            if (isMe) localSlot = i;
            // Two players on one class share a color family, so say who's who.
            if (!isMe && ClassRepeats(_players, i) && PlayerName(_players[i]) is { } who) name = $"{name} · {who}";
            labels[i] = isMe && _players.Count > 1 ? $"{name} (You)" : name;
        }

        _summaryActive = false; // a new fight clears any lingering end-of-combat summary
        _summaryAvailable = false;
        _recapVisible = false;   // the recap is an out-of-combat view
        _combatResolved = false; // until enemies/player actually die, this combat hasn't ended naturally
        _allEnemiesDeadFrames = 0;
        _frozenForecast = null;
        _localSlot = localSlot;
        _fightMeta = CaptureFightMeta(rs, SafeCombatState());
        _tracker.Reset(_players.Count, labels, localSlot);
        VfxMoments.Reset(me);
        _pending.Clear();
        _dotTags.Clear();
        UnsubscribeAll();
        EnsureViews();
        GD.Print($"[STS2 Damage] combat start ({_players.Count} player(s))");
    }

    private static void OnCombatEnd()
    {
        UnsubscribeAll();
        _pending.Clear();
        _dotTags.Clear();
        _bars?.Hide();
        _tooltip?.Hide();
        _forecast?.Hide(); _forecastLedger?.Hide();
        // Fold this combat into the run-wide aggregate before the next combat's Reset() wipes the tracker.
        if (_tracker.HasData())
        {
            _run.Fold(_tracker.SourceSnapshot(), _tracker.Snapshot(), _fightMeta);
            TryWriteRunStats();
        }
        // Auto-show the breakdown as an end-of-combat summary (until dismissed / map / next fight), but
        // only for a real win/loss — not when the player abandons combat (e.g. quits to the menu).
        _summaryActive = _summaryOnEnd && _combatResolved && _tracker.HasData();
        _summaryAvailable = _summaryActive;
        _rewardsSeen = false;
        if (!_summaryActive) _detail?.SetVisible(false);
        GD.Print($"[STS2 Damage] combat end (summary={_summaryActive})");
    }

    // ===== Per-run damage stats file =====

    // The save library's run id for the live run (start time, seed, players' SteamID64s), plus each slot's
    // SteamID64 and the local player's. Null when any part can't be read: then nothing is written.
    // This Steam account's SteamID64, from the game's PlatformUtil.GetLocalPlayerId(PrimaryPlatform), read by name
    // so versions without it just skip single-player stats. Cached; null when unavailable.
    private static string? _localSteamId;
    private static bool _localSteamResolved;
    private static string? LocalSteamId()
    {
        if (_localSteamResolved) return _localSteamId;
        _localSteamResolved = true;
        try
        {
            var util = AccessTools.TypeByName("MegaCrit.Sts2.Core.Platform.PlatformUtil");
            var platform = util?.GetProperty("PrimaryPlatform")?.GetValue(null);
            var get = util?.GetMethod("GetLocalPlayerId", new[] { platform?.GetType() ?? typeof(object) });
            if (get?.Invoke(null, new[] { platform }) is ulong id && id != 0)
                _localSteamId = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { }
        if (_localSteamId == null) GD.Print("[STS2 Damage] local Steam ID not found: no single-player run stats file");
        return _localSteamId;
    }

    private static RunIdent? ReadRunIdentity(RunState? rs)
    {
        if (rs == null) return null;
        try
        {
            if (!_startTimeResolved)
            {
                _startTimeResolved = true;
                // RunManager keeps the save's StartTime in a private field (no public getter).
                _startTimeField = AccessTools.Field(typeof(RunManager), "_startTime");
                if (_startTimeField == null) GD.Print("[STS2 Damage] run start time not found (game changed?): no run stats file");
            }
            if (_startTimeField?.GetValue(RunManager.Instance) is not long start || start <= 0) return null;
            if (InstanceGet(InstanceGet(rs, "Rng"), "StringSeed") is not string seed || seed.Length == 0)
            {
                ErrorLog.NoteOnce("run seed (Rng.StringSeed) not found: no run stats file or Share for this run");
                return null;
            }
            var players = rs.Players;
            if (players == null || players.Count == 0) return null;
            var raw = new string[players.Count];
            for (int i = 0; i < players.Count; i++)
            {
                ulong id = players[i].NetId;
                if (id == 0) return null;
                raw[i] = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            string? me = LocalContext.GetMe(rs)?.NetId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Single-player: the lone player's net id is a placeholder (1); the run belongs to this Steam account.
            string? localSteam = raw.Length == 1 && raw[0] == me ? LocalSteamId() : null;
            var ids = RunId.ResolveSolo(raw, localSteam);
            if (ids == null) return null;
            string? local = ids.Length == 1 && raw.Length == 1 ? ids[0] : me;
            return new RunIdent(RunId.Compute(start.ToString(System.Globalization.CultureInfo.InvariantCulture), seed, ids), ids, local);
        }
        catch { return null; }
    }

    // After a finished combat: add it to <user dir>/damage-charts/runs/<run_id>.json (temp file +
    // rename). Never throws; a failure costs this fight's entry only.
    private static void TryWriteRunStats()
    {
        try
        {
            if (!_runStatsFile || !_combatResolved) return; // an abandoned fight is replayed on continue
            var ident = _fightRun;
            // recorded_by must be one of the run's players (the server checks it).
            if (ident == null || ident.SteamIds.Length < 1 || string.IsNullOrEmpty(ident.LocalId)
                || Array.IndexOf(ident.SteamIds, ident.LocalId) < 0) return;
            var now = ReadRunIdentity(SafeRunState());
            if (now == null || now.RunId != ident.RunId)
            {
                GD.Print("[STS2 Damage] run stats: run changed during combat, fight not written");
                return;
            }
            if (_run.RunKey != ident.RunId)
            {
                ErrorLog.NoteOnce("run stats: recap is keyed to another run, fight not written");
                return;
            }
            if (!_run.TryGetFight(_run.FightCount, out var meta, out var sources)) return;
            if (sources.Length != ident.SteamIds.Length)
            {
                ErrorLog.NoteOnce($"run stats: {sources.Length} player slot(s) but {ident.SteamIds.Length} Steam ID(s), fight not written");
                return;
            }

            int floor = meta.Floor;
            if (floor > 0 && ident.RunId == _statsRunId && floor == _statsFloor) _statsFloorCount++;
            else _statsFloorCount = 1;
            _statsRunId = ident.RunId;
            _statsFloor = floor;

            string dir = Path.Combine(OS.GetUserDataDir(), "damage-charts", "runs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, ident.RunId + ".json");
            var fights = new List<System.Text.Json.Nodes.JsonObject>();
            if (File.Exists(path))
            {
                var parsed = RunStatsFile.ParseFights(File.ReadAllText(path), ident.RunId);
                if (parsed != null) fights = parsed;
                else
                {
                    // Not a v1 file for this run: keep it aside rather than silently losing it.
                    try { File.Move(path, path + ".bad", overwrite: true); } catch { }
                    GD.PrintErr($"[STS2 Damage] run stats: unreadable {path}, moved to .bad and started over");
                }
            }
            string? text = RunStatsFile.AddFight(fights, ident.RunId, ident.LocalId!, _modVersion,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), meta, sources, ident.SteamIds, _statsFloorCount, out var outcome);
            if (text == null)
            {
                GD.Print($"[STS2 Damage] run stats: fight not written ({outcome})");
                return;
            }
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            GD.Print($"[STS2 Damage] run stats: {outcome} floor {floor} -> {path}");
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] run stats write failed: {ex.Message}"); }
    }

    private static bool _layersDumped;

    // One-shot: locate the game nodes we want to draw onto (player health bar, End Turn button) and log
    // their type, screen rect, and any readable HP so we can identify the player's bar and anchor overlays.
    private static void DumpTargetNodes(Node root)
    {
        try
        {
            foreach (var n in FindByTypeName(root, "NHealthBar"))
            {
                string rect = n is Control c ? c.GetGlobalRect().ToString() : "(not Control)";
                string cr = "";
                try
                {
                    object? creature = InstanceGet(n, "Creature") ?? InstanceGet(n.GetParentOrNull<Node>(), "Creature");
                    if (creature != null) cr = $" creature.hp={ForecastReaderPeekHp(creature)} isPlayer={InstanceGet(creature, "IsPlayer")}";
                }
                catch { }
                GD.Print($"[STS2 Damage] NODE NHealthBar rect={rect} vis={(n as CanvasItem)?.Visible}{cr}");
            }
            foreach (var n in FindByTypeName(root, "NEndTurnButton"))
            {
                string rect = n is Control c ? c.GetGlobalRect().ToString() : "(not Control)";
                GD.Print($"[STS2 Damage] NODE NEndTurnButton type={n.GetType().Name} rect={rect}");
            }
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] node dump failed: {ex.Message}"); }
    }

    private static List<Node> FindByTypeName(Node start, string typeName)
    {
        var found = new List<Node>();
        void Walk(Node n)
        {
            try
            {
                if (n.GetType().Name == typeName) found.Add(n);
                foreach (var c in n.GetChildren()) Walk(c);
            }
            catch { }
        }
        Walk(start);
        return found;
    }

    private static object? InstanceGet(object? obj, string prop)
    {
        if (obj == null) return null;
        try { return obj.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(obj); }
        catch { return null; }
    }

    private static object? ForecastReaderPeekHp(object creature)
    {
        try { return creature.GetType().GetProperty("CurrentHp")?.GetValue(creature); } catch { return null; }
    }

    // One-shot: log every CanvasLayer in the tree with its Layer number and any hover-tip/preview/global-ui
    // descendants, so we can place our overlays just below the game's tooltip layer.
    private static void DumpCanvasLayers(Node node, int depth)
    {
        try
        {
            if (node is CanvasLayer cl)
            {
                string kids = "";
                foreach (var t in new[] { "NHoverTip", "NCardPreview", "NGlobalUi", "NTooltip", "HoverTip", "Preview", "Modal" })
                    if (FindDescendantNamed(cl, t)) { kids += t + " "; }
                GD.Print($"[STS2 Damage] LAYER name='{cl.Name}' layer={cl.Layer} contains=[{kids}]");
            }
            foreach (var c in node.GetChildren()) DumpCanvasLayers(c, depth + 1);
        }
        catch { }
    }

    private static bool FindDescendantNamed(Node start, string sub)
    {
        try
        {
            foreach (var c in start.GetChildren())
            {
                if (c.GetType().Name.Contains(sub) || c.Name.ToString().Contains(sub)) return true;
                if (c is not CanvasLayer && FindDescendantNamed(c, sub)) return true; // don't cross into nested layers
            }
        }
        catch { }
        return false;
    }

    private static void EnsureViews()
    {
        try
        {
            var root = ((SceneTree)Engine.GetMainLoop()).Root;
            UiTheme.EnsureFont(root);
            UiTheme.EnsurePanelStyle(root);
            if (ForecastReader.Debug && !_layersDumped) { _layersDumped = true; DumpCanvasLayers(root, 0); DumpTargetNodes(root); }
            Vector2? savedPos = (_posXFrac.HasValue && _posYFrac.HasValue) ? new Vector2(_posXFrac.Value, _posYFrac.Value) : null;
            // A view whose content the game freed (mounted beneath its hover tips) still owns its layer.
            if (_bars != null && !_bars.IsValid()) { _bars.Dispose(); _bars = null; }
            if (_bars == null) _bars = new DamageChartView(root, savedPos);
            Vector2? fcPos = (_fcPosXFrac.HasValue && _fcPosYFrac.HasValue) ? new Vector2(_fcPosXFrac.Value, _fcPosYFrac.Value) : (Vector2?)null;
            if (_forecast != null && !_forecast.IsValid()) { _forecast.Dispose(); _forecast = null; }
            if (_forecast == null) _forecast = new DamageForecastView(root, fcPos);
            if (_forecast != null) _forecast.UiScaleMul = _uiScale;
            if (_forecastLedger == null || !_forecastLedger.IsValid()) _forecastLedger = new ForecastLedgerView(root);
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
    // Live "the fight is ending right now" — the game's own victory/defeat flags, or the local player
    // being dead. Deliberately excludes the all-enemies-dead heuristic so splitting enemies don't read
    // as resolved mid-fight. Used to hide the in-combat HUD; the sticky _combatResolved drives the summary.
    private static bool IsFightEndingNow()
    {
        try { var cm = CombatManager.Instance; if (cm != null && (cm.IsOverOrEnding || cm.IsEnding)) return true; }
        catch { }
        try { if (_localSlot >= 0 && _localSlot < _players.Count && _players[_localSlot]?.Creature?.IsDead == true) return true; }
        catch { }
        return false;
    }

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
            // Debounce: splitting enemies (Phrog -> wrigglers) leave a frame with all enemies momentarily
            // dead before the spawns register. Require the empty state to persist so we don't latch
            // "resolved" and hide the HUD mid-fight. Real wins hold and resolve within ~0.5s.
            if (anyEnemy && !anyEnemyAlive) { if (++_allEnemiesDeadFrames >= 30) return true; }
            else _allEnemiesDeadFrames = 0;
        }
        catch { }
        return false;
    }
    private static int _allEnemiesDeadFrames;

    private static void OnBlockChanged(Creature creature, int oldBlock, int newBlock)
    {
        if (_disabled) return;
        try
        {
            VfxMoments.OnBlockChanged(creature, oldBlock, newBlock);
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

            // One HP change per tag: a stale tag from an earlier hit must not label a later HP change that
            // didn't come through Damage (e.g. a Sandpit kill).
            _pending.Remove(creature, out var pend);
            if (pend.Label != null && _frame - pend.Frame > PendingTtlFrames) pend = default;

            if (creature.IsPlayer)
            {
                int slot = SlotOf(creature.Player);
                if (slot < 0) return;
                string attacker; string? attackerId;
                if (pend.Dealer != null && pend.Dealer.IsMonster)
                {
                    attacker = TextHelper.SafeGetText(() => pend.Dealer!.Monster!.Title) ?? "Enemy";
                    attackerId = ModelIdOf(InstanceGet(pend.Dealer, "Monster"));
                }
                else if (pend.Label != null) { attacker = pend.Label; attackerId = pend.Id; }
                else { var a = ConsumeActive(); attacker = a.Label ?? "Unknown"; attackerId = a.Id; }
                string victim = MaybeName(creature);
                _tracker.AddTaken(round, slot, lost, attacker, null, attackerId);
                if (ForecastReader.Debug) GD.Print($"[STS2 Damage] HIT taken src={attacker} amt={lost} round={round} frame={_frame}");
                VfxMoments.OnTaken(creature, lost);
                _tracker.AddLog(round, $"{attacker} → {victim}  {lost}", true);
                return;
            }

            // Enemy lost HP -> damage dealt by a player (or their pet/summon, e.g. Necrobinder's Osty).
            var dealer = pend.Dealer;
            int dslot;
            if (dealer != null && dealer.IsPlayer) dslot = SlotOf(dealer.Player);
            else if (dealer != null && dealer.IsPet) dslot = SlotOf(dealer.PetOwner); // pet → its owner
            else if (dealer != null && dealer.IsMonster) return; // genuine enemy-on-enemy, not player damage
            else dslot = -1;                                      // dealer null: the source's player, below
            string src; string? icon; string? srcId;
            Player? owner = null;
            if (pend.Label != null) { src = pend.Label; icon = pend.Icon; srcId = pend.Id; }
            else { var a = ConsumeActive(); src = a.Label ?? "Status"; icon = a.Icon; owner = a.Owner; srcId = a.Id; } // Doom/Kill & other non-Damage paths
            if (dslot < 0) dslot = SlotOf(owner);
            if (dslot < 0) dslot = ActiveSlot();                  // nothing says whose it was: local player
            _tracker.AddDealt(round, dslot, lost, src, icon, srcId);
            if (ForecastReader.Debug) GD.Print($"[STS2 Damage] HIT dealt src={src} amt={lost} round={round} frame={_frame}");
            if (dslot == _localSlot) VfxMoments.OnDealt(creature, lost, src, icon);
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

    private static (string? Label, string? Icon, Player? Owner, string? Id) ConsumeActive()
    {
        if (ActiveFresh()) { var v = _activeSource!.Value; _activeSource = null; return (v.Label, v.Icon, v.Owner, v.Id); }
        return (null, null, null, null);
    }

    // A game model's id as the game writes it ("CARD.STRIKE_SILENT"), read by reflection; null when absent.
    private static string? ModelIdOf(object? model)
    {
        try
        {
            string? s = InstanceGet(model, "Id")?.ToString();
            if (string.IsNullOrEmpty(s) || s.IndexOf('.') <= 0 || s == "NONE.NONE") return null;
            return s;
        }
        catch { return null; }
    }

    // Which player a damage source belongs to. A power is credited to whoever applied it (the Silent's
    // Poison sits on the enemy, so its Owner is the enemy), falling back to its Owner when that's a player
    // (a player's own power); relics, orbs and potions have a Player Owner. Null when unknown.
    private static Player? SourcePlayer(object model)
    {
        try
        {
            // A monster-applied power (an enemy's Poison on you) belongs to no player.
            if (model is PowerModel power) return power.Applier != null ? PlayerOf(power.Applier) : PlayerOf(power.Owner);
            var owner = model.GetType().GetProperty("Owner")?.GetValue(model);
            return owner as Player ?? PlayerOf(owner as Creature);
        }
        catch { return null; }
    }

    private static Player? PlayerOf(Creature? c) => c == null ? null : c.IsPlayer ? c.Player : c.IsPet ? c.PetOwner : null;

    private static Player? DoomApplier(Creature c)
    {
        try
        {
            foreach (var p in c.Powers)
                if (p.GetType().Name == "DoomPower") return PlayerOf(p.Applier);
        }
        catch { }
        return null;
    }

    private static bool SameClass(Player a, Player b)
    {
        try { return a.Character != null && b.Character != null && a.Character.GetType() == b.Character.GetType(); }
        catch { return false; }
    }

    private static bool ClassRepeats(IReadOnlyList<Player> players, int i)
    {
        for (int j = 0; j < players.Count; j++) if (j != i && SameClass(players[i], players[j])) return true;
        return false;
    }

    // Class color for each player; a second, third... player on the same class gets a lighter, then
    // darker, shade so their bars stay distinguishable. Falls back to the seat palette if the game
    // doesn't provide a color.
    internal static Color[] PlayerColors(IReadOnlyList<Player> players)
    {
        var colors = new Color[Math.Max(1, players.Count)];
        for (int i = 0; i < players.Count; i++)
        {
            Color c = UiTheme.Players[i % UiTheme.Players.Length];
            try { if (players[i].Character is { } ch) c = ch.NameColor; } catch { }
            int repeat = 0;
            for (int j = 0; j < i; j++) if (SameClass(players[i], players[j])) repeat++;
            colors[i] = ShadeFor(c, repeat);
        }
        return colors;
    }

    // Shades keep the class hue: a repeat changes saturation/brightness only, so it doesn't wash out
    // toward the cream text color the way Lightened() does on an already-light class color.
    internal static Color ShadeFor(Color c, int repeat) => repeat switch
    {
        0 => c,
        1 => Color.FromHsv(c.H, c.S * 0.45f, Math.Min(1f, c.V * 1.1f + 0.1f), c.A),
        2 => Color.FromHsv(c.H, Math.Min(1f, c.S * 1.1f), c.V * 0.65f, c.A),
        _ => Color.FromHsv(c.H, c.S * 0.7f, Math.Max(0.35f, c.V * (1f - 0.1f * repeat)), c.A),
    };

    private static string? PlayerName(Player p)
    {
        try
        {
            var name = PlatformUtil.GetPlayerNameRaw(RunManager.Instance.NetService.Platform, p.NetId);
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch { return null; }
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

    // Type name of the top overlay screen (NOverlayStack.Instance.Peek()), or null if none. The rewards list
    // (NRewardsScreen) is pushed in ShowScreen and removed when the player proceeds; a card choice
    // (NCardRewardSelectionScreen) is pushed on top of it.
    private static string? TopOverlayName()
    {
        try
        {
            ResolveBlockers();
            var stack = GetInstance(_tOverlay);
            return stack?.GetType().GetMethod("Peek", Type.EmptyTypes)?.Invoke(stack, null)?.GetType().Name;
        }
        catch { return null; }
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
        try { _forecast?.Dispose(); } catch { }
        try { _share?.Dispose(); } catch { }
        _bars = null; _detail = null; _tooltip = null; _forecast = null; _share = null; _gameOverScreen = null;
    }
}
