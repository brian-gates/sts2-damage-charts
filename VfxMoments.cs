using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace STS2_DamageCharts;

// Opt-in "big moment" flourishes (config "vfx_moments", default off) built from the GAME'S OWN combat VFX:
//   - new biggest single hit this combat -> NDamageNumVfx (gold "BEST n", spawned above the game's own number)
//   - an enemy turn whose attacks your block fully absorbed -> NDamageBlockedVfx (gold, enlarged) over you
//   - a new source taking the lead in your by-source breakdown -> NUiFlashVfx of that source's icon over you
// Capture hooks only RECORD (no nodes are touched inside the game's damage pipeline); spawning happens in
// Tick on the main thread, coalesced and rate-limited. Purely cosmetic: never touches game state or the log.
// Inert unless both vfx_moments and native_visuals are on; any missing scene/type/member degrades to nothing.
//
// Verified against sts2.dll v0.107.1 (MegaCrit.Sts2.Core.Nodes.*):
//   NDamageNumVfx.Create(Vector2 globalPosition, int damage)  sets private _text = damage.ToString(), shown in _Ready
//   NDamageBlockedVfx.Create(Creature)  positions at NCreature.VfxSpawnPosition (+jitter); NREs if no creature node
//   NUiFlashVfx.Create(Texture2D, Color) + StartVfx(): full-rect Control, so it is parented to a sized holder
//   NCombatRoom.Instance.CombatVfxContainer is where CreatureCmd.Damage adds its own damage numbers / Blocked text.
//   All three instantiate via PreloadManager.Cache (falls back to a plain load), and use Rng.Chaotic, the game's
//   cosmetic-only RNG, so spawning them cannot shift seeded gameplay randomness.
internal static class VfxMoments
{
    public static bool Enabled;   // config "vfx_moments" (default false)

    private static bool Active => Enabled && GameAssets.Enabled;

    private const string BlockedScene = "res://scenes/vfx/vfx_blocked_text.tscn";
    private const string FlashScene = "res://scenes/vfx/ui_flash_vfx.tscn";

    private const int MinBestHit = 10;          // a "new best" must be at least this big...
    private const ulong BestSettleFrames = 20;  // ...and stand for ~1/3s (after the game's own number, coalescing multi-hits)
    private const ulong BestCooldownMs = 2500;
    private const long MinLeadTotal = 15;       // ignore lead swaps while totals are still tiny
    private const ulong LeadCooldownMs = 6000;
    private const ulong AnyGapMs = 700;         // never stack two moments on top of each other
    private const ulong PendingTtlMs = 2000;    // a moment that can't spawn promptly is dropped, not deferred

    private static Player? _me;
    // Best hit
    private static int _best;
    private static (Creature Target, int Amount, ulong Frame, ulong At)? _pendingBest;
    private static ulong _lastBestMs;
    // Perfect block (per enemy turn, evaluated when the round number advances)
    private static int _round = -1;
    private static bool _attacked, _hurt;
    private static int _absorbed;
    private static ulong? _pendingBlockAt;
    // Lead change
    private static readonly Dictionary<string, long> _totals = new();
    private static string? _leader;
    private static (string Icon, ulong At)? _pendingLead;
    private static ulong _lastLeadMs;
    private static ulong _lastAnyMs;

    public static void Reset(Player? me)
    {
        _me = me;
        _best = 0; _pendingBest = null;
        _round = -1; _attacked = false; _hurt = false; _absorbed = 0; _pendingBlockAt = null;
        _totals.Clear(); _leader = null; _pendingLead = null;
    }

    // ===== Capture (called from the mod's hooks; record only) =====

    // From DamagePrefix (per target): a monster is about to damage this creature.
    public static void NoteAttack(Creature? target, Creature? dealer)
    {
        if (!Active || target == null || dealer == null) return;
        try
        {
            if (!dealer.IsMonster || !ReferenceEquals(target, MyCreature())) return;
            // Enemy turn only (not e.g. retaliation damage during your own turn).
            if (IsEnemyTurn()) _attacked = true;
        }
        catch { }
    }

    // From OnBlockChanged: block lost during an enemy turn, once you've been attacked, is block absorbing hits.
    // (Not a per-call frame window: CreatureCmd.Damage awaits hooks before DamageBlockInternal/LoseHpInternal.)
    public static void OnBlockChanged(Creature creature, int oldBlock, int newBlock)
    {
        if (!Active || newBlock >= oldBlock) return;
        try
        {
            if (ReferenceEquals(creature, MyCreature()) && InAttackWindow()) _absorbed += oldBlock - newBlock;
        }
        catch { }
    }

    // From OnHpChanged (player branch): any HP lost in that same stretch spoils the perfect block.
    public static void OnTaken(Creature creature, int lost)
    {
        if (!Active || lost <= 0) return;
        try { if (ReferenceEquals(creature, MyCreature()) && InAttackWindow()) _hurt = true; }
        catch { }
    }

    // From OnHpChanged (dealt branch), local player only.
    public static void OnDealt(Creature target, int amount, string source, string? icon)
    {
        if (!Active || amount <= 0) return;
        try
        {
            ulong now = Time.GetTicksMsec();
            if (amount > _best)
            {
                // The first hit of a fight sets the bar silently; only beating a previous best is a moment.
                if (_best > 0 && amount >= MinBestHit)
                    _pendingBest = (target, amount, Engine.GetProcessFrames(), now);
                _best = amount;
            }

            _totals.TryGetValue(source, out long cur);
            long total = cur + amount;
            _totals[source] = total;
            if (_leader == null) { _leader = source; return; }  // first source isn't "taking" the lead
            if (_leader == source) return;
            _totals.TryGetValue(_leader, out long lead);
            if (total > lead)
            {
                _leader = source;
                if (total >= MinLeadTotal && icon != null) _pendingLead = (icon, now);
            }
        }
        catch { }
    }

    // ===== Spawn (per frame, in combat) =====

    public static void Tick(int round, bool suppressed)
    {
        if (!Active) return;
        try
        {
            ulong now = Time.GetTicksMsec();
            // Round advanced => the enemy turn just resolved. Judge it once.
            if (round != _round)
            {
                if (_round >= 0 && _attacked && !_hurt && _absorbed > 0) _pendingBlockAt = now;
                _round = round; _attacked = false; _hurt = false; _absorbed = 0;
            }

            if (_pendingBest is { } pb && now - pb.At > PendingTtlMs) _pendingBest = null;
            if (_pendingBlockAt is { } pba && now - pba > PendingTtlMs) _pendingBlockAt = null;
            if (_pendingLead is { } pl && now - pl.At > PendingTtlMs) _pendingLead = null;

            // A menu/takeover is up or the fight is ending: drop moments rather than replaying them later.
            if (suppressed || IsCombatEnding()) { _pendingBest = null; _pendingBlockAt = null; _pendingLead = null; return; }
            if (now - _lastAnyMs < AnyGapMs) return;

            if (_pendingBest is { } b && Engine.GetProcessFrames() - b.Frame >= BestSettleFrames && now - _lastBestMs >= BestCooldownMs)
            {
                _pendingBest = null;
                if (SpawnBestHit(b.Target, b.Amount)) { _lastBestMs = now; _lastAnyMs = now; }
                return;
            }
            if (_pendingBlockAt.HasValue)
            {
                _pendingBlockAt = null;
                if (SpawnPerfectBlock()) _lastAnyMs = now;
                return;
            }
            if (_pendingLead is { } l && now - _lastLeadMs >= LeadCooldownMs)
            {
                _pendingLead = null;
                if (SpawnLeadFlash(l.Icon)) { _lastLeadMs = now; _lastAnyMs = now; }
            }
        }
        catch { }
    }

    private static bool SpawnBestHit(Creature target, int amount)
    {
        if (GameAssets.Scene(GameAssets.Paths.DamageNumScene) == null) return false;
        var container = VfxContainer();
        var at = SpawnPos(target);
        if (container == null || at == null || _mDamageNumCreate == null) return false;
        // The game spawns its own number at VfxSpawnPosition + (0,-100); start ours well above it.
        if (_mDamageNumCreate.Invoke(null, new object[] { at.Value + new Vector2(0f, -250f), amount }) is not Node2D vfx) return false;
        try { _fDamageNumText?.SetValue(vfx, $"BEST {amount}"); } catch { }
        container.AddChild(vfx);
        // Root modulate tweens red -> cream; a gold self-modulate on the label keeps it from reading as a hit.
        if (vfx.GetNodeOrNull("Label") is Control label) label.SelfModulate = UiTheme.Gold;
        return true;
    }

    private static bool SpawnPerfectBlock()
    {
        if (GameAssets.Scene(BlockedScene) == null) return false;
        var container = VfxContainer();
        var mine = MyCreature();
        // Create NREs on a missing creature node, so require one (and its interactable check) up front.
        if (container == null || mine == null || SpawnPos(mine) == null || _mBlockedCreate == null) return false;
        if (_mBlockedCreate.Invoke(null, new object[] { mine }) is not Node2D vfx) return false;
        vfx.Position += new Vector2(0f, -110f);   // above the per-hit "Blocked" texts the game already showed
        container.AddChild(vfx);
        if (vfx.GetNodeOrNull("Label") is Control label)
        {
            label.SelfModulate = UiTheme.Gold;    // its own tween drives modulate (blue -> white), not self_modulate
            label.PivotOffset = label.Size * 0.5f;
            label.Scale = Vector2.One * 1.35f;
        }
        return true;
    }

    private static bool SpawnLeadFlash(string iconPath)
    {
        if (GameAssets.Scene(FlashScene) == null) return false;
        var tex = GameAssets.Texture(iconPath);
        var container = VfxContainer();
        var mine = MyCreature();
        var at = mine != null ? SpawnPos(mine) : null;
        if (tex == null || container == null || at == null || _mFlashCreate == null || _mFlashStart == null) return false;
        if (_mFlashCreate.Invoke(null, new object[] { tex, new Color(UiTheme.Gold, 0.9f) }) is not Control flash) return false;

        // NUiFlashVfx is a full-rect Control (the game parents it to the widget it flashes), so give it a
        // fixed-size holder above your head. The holder frees itself when the flash does.
        const float size = 120f;
        var holder = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Size = new Vector2(size, size) };
        container.AddChild(holder);
        holder.GlobalPosition = at.Value + new Vector2(-size / 2f, -260f - size / 2f);
        holder.AddChild(flash);
        flash.TreeExited += () => { try { if (GodotObject.IsInstanceValid(holder)) holder.QueueFree(); } catch { } };
        if (_mFlashStart.Invoke(flash, null) is Task t)
            t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return true;
    }

    // ===== Game lookups (reflection by name; any miss => null => no moment) =====

    private static bool _resolved;
    private static MethodInfo? _mDamageNumCreate, _mBlockedCreate, _mFlashCreate, _mFlashStart, _mGetCreatureNode;
    private static FieldInfo? _fDamageNumText;
    private static PropertyInfo? _pRoomInstance, _pVfxContainer, _pVfxSpawnPos, _pInteractable;

    private static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        try
        {
            var tNum = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Vfx.NDamageNumVfx");
            var tBlocked = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Vfx.NDamageBlockedVfx");
            var tFlash = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Vfx.NUiFlashVfx");
            var tRoom = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom");
            var tCreature = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NCreature");
            if (tNum != null)
            {
                _mDamageNumCreate = AccessTools.Method(tNum, "Create", new[] { typeof(Vector2), typeof(int) });
                _fDamageNumText = AccessTools.Field(tNum, "_text");
            }
            if (tBlocked != null) _mBlockedCreate = AccessTools.Method(tBlocked, "Create", new[] { typeof(Creature) });
            if (tFlash != null)
            {
                _mFlashCreate = AccessTools.Method(tFlash, "Create", new[] { typeof(Texture2D), typeof(Color) });
                _mFlashStart = AccessTools.Method(tFlash, "StartVfx");
            }
            if (tRoom != null)
            {
                _pRoomInstance = AccessTools.Property(tRoom, "Instance");
                _pVfxContainer = AccessTools.Property(tRoom, "CombatVfxContainer");
                _mGetCreatureNode = AccessTools.Method(tRoom, "GetCreatureNode", new[] { typeof(Creature) });
            }
            if (tCreature != null)
            {
                _pVfxSpawnPos = AccessTools.Property(tCreature, "VfxSpawnPosition");
                _pInteractable = AccessTools.Property(tCreature, "IsInteractable");
            }
            if (_mDamageNumCreate == null || _mBlockedCreate == null || _mFlashCreate == null || _pVfxContainer == null || _pVfxSpawnPos == null)
                GD.Print("[STS2 Damage] vfx_moments: some game VFX members not found; those moments are skipped");
        }
        catch { }
    }

    private static object? Room()
    {
        Resolve();
        try { return _pRoomInstance?.GetValue(null); } catch { return null; }
    }

    private static Node? VfxContainer()
    {
        try
        {
            var room = Room();
            if (room == null || _pVfxContainer?.GetValue(room) is not Node n) return null;
            return GodotObject.IsInstanceValid(n) && n.IsInsideTree() ? n : null;
        }
        catch { return null; }
    }

    // The creature's on-screen VFX anchor, or null if it has no live, interactable node (dead/removed/hidden).
    private static Vector2? SpawnPos(Creature c)
    {
        try
        {
            var room = Room();
            if (room == null || _mGetCreatureNode == null) return null;
            if (_mGetCreatureNode.Invoke(room, new object[] { c }) is not Node node || !GodotObject.IsInstanceValid(node)) return null;
            if (_pInteractable?.GetValue(node) is false) return null;
            return _pVfxSpawnPos?.GetValue(node) is Vector2 v ? v : null;
        }
        catch { return null; }
    }

    private static Creature? MyCreature()
    {
        try { return _me?.Creature; } catch { return null; }
    }

    private static bool InAttackWindow() => _attacked && IsEnemyTurn();

    private static bool IsEnemyTurn()
    {
        try { return CombatManager.Instance?.DebugOnlyGetState()?.CurrentSide == CombatSide.Enemy; } catch { return false; }
    }

    private static bool IsCombatEnding()
    {
        try { return CombatManager.Instance?.IsEnding == true; } catch { return false; }
    }
}
