# Known issues

## Power-source attribution hooks could deadlock/crash the game (fixed: curated hooks, on by default)

**Status:** Fixed. The blanket patching was replaced by a curated list (see the 2026-10-22 update) and,
after the 2026-10-30 soak test below, `source_hooks` defaults **on**. Kept for the history and evidence.

**Symptom:** Intermittent hard hang (game shows "not responding") and/or repeated
`[STS2 Damage] disabled after repeated errors: Bad IL range.` log errors — during combat, on opening
combat stats, or post-combat.

**Root cause:** `TryApplyPowerSourceHooks()` Harmony-patches every overridden `void`/`Task` hook method
of every `PowerModel`/`RelicModel`/`OrbModel`/`PotionModel` subclass (~791 methods) to stamp the acting
power as the damage source. This blanket patching intermittently corrupts method metadata, producing
either a `Bad IL range` exception or a hard deadlock in the CLR reflection/JIT. A live `sample` of the
hung process showed the main thread blocked in `SignatureNative::GetSignature` (libcoreclr), reached from
the per-frame `ProcessFrame` callback. Raw evidence: [`sts2_hang_sample_diag1.txt`](sts2_hang_sample_diag1.txt).

**Confirmation:** With `source_hooks: false`, the game is stable across scenarios that previously hung
(user-confirmed). With it on, the hang was intermittent.

**Mitigation (shipped):** `source_hooks` defaults to `false`. With it off, DoT/relic/orb/potion damage is
labeled generically (`Status`/`Other`), but all other stats (card damage, totals, dealt/taken/heal/block,
per-fight chart, recap, run history) work and the mod is stable. Opt in with `"source_hooks": true` at
your own risk.

**Desired fix:** by-name DoT/relic/orb/potion attribution without the deadlock-prone blanket patching.
Notes for whoever picks it up:
- There is no "current acting power" signal on `CombatManager`/`Hook`/`CombatState`.
- DoTs tick in async methods, so stack-walking in the existing `CreatureCmd.Damage` prefix won't find
  the power frame (which is why the original author patched everything).
- Candidates to investigate: a curated set of known damage-dealing hook methods; patching only safe
  method shapes; or a different game-side signal.

**Update 2026-10-22: curated hook list (in testing).** The blanket patch is replaced by
`DamageSourceHooks`: 59 methods on 52 types that a static call-graph sweep of `sts2.dll` v0.107.1 found
can reach `CreatureCmd.Damage`/`Kill`/`SetCurrentHp`/`LoseMaxHp` (via REA `inspect-managed-members`,
following async state machines and same-type helpers). Pickup-time `AfterObtained` hooks and Fur Coat
are excluded. Missing names are logged as "not found (game changed?)"; re-run the sweep after patches.
Status so far: 59/59 resolve and patch on v0.107.1; several combats ran without `Bad IL range` errors.
The startup freezes seen during this testing were the Sentry bug below (they reproduced with
`source_hooks: false` and with a different mod loaded alone), not these hooks. `source_hooks` still
defaults to `false` until by-name labels are confirmed in game.
**Update 2026-10-30: on by default.** `scripts/soak_source_hooks.py` (17 launches, 50 staged fights,
alternating the game's normal and fast speed, 45 of the hooked items granted through the dev console):
0 freezes, 0 crashes, 0 `Bad IL range`, 0 mod errors, 0 game exceptions with the mod on the stack, and
no hit credited to a wrong name. 19 items were named on their hits; the rest fall back to `Status` (see
issue #27). Not exercised: orbs (Ironclad-only test), and Sandpit, which throws inside the game's own
code when granted outside its boss fight.
Known gap: 11 of the hooks await (animation/`Cmd.Wait`) before dealing damage (The Bomb, Magic Bomb,
Thunder, Serpent Form, Panache, Inferno turn-start, Explosive Ampoule, The Gambit, Sandpit, Precarious
Shears, Doom's kill path), so their stamp can be older than the 1-frame `ActiveFresh()` window and the
hit may still read "Status". Doom is covered separately by `DoomKillPrefix`.

## Modded startup hard-freezes the game (game-side Sentry bug, NOT our mod)

**Update 2026-10-22: trigger pinned down, workaround shipped in the mod.** This is **not
Workshop-specific**. It reproduced 5 times in 6 launches with a **manual** `mods/` install (machine on
low battery, which likely shifts startup timing), including with `source_hooks: false` and with only an
unrelated mod (`STS2_NotTheBees`) loaded. The trigger is `SentryService.AfterGameInit` →
`ShouldStayAliveAfterInit()` is false when modded → `SentryService.Shutdown()`, which disposes the .NET
SDK and then calls `shutdown` on the native `SentryInit` node. The native shutdown leaves the per-frame
CFRunLoop observer registered, and every hang sampled had the main thread stuck in that observer on
`Object::get_instance_id()` (same stack as below), apparently touching an object the shutdown freed.
Whether a given launch hangs is timing-dependent and **not** predicted by log order: an earlier note here
said hangs happen when the shutdown lands before `Applying display settings...`, but 4 later passing
launches had that same order (corrected 2026-10-23). The bad streak (5/6) coincided with a nearly empty
laptop battery; after charging, 4/4 launches of the unpatched build passed, so the trigger condition is
not pinned down. The earlier unmodded hang *on quit* is likely the same thing: quitting calls the same
native `shutdown`.
**Workaround (in `DamageChartsMod.TryApplySentryShutdownWorkaround`):** a Harmony prefix on
`SentryService.Shutdown` nulls `SentryService._sentryInit` first, so the game still disposes the .NET
SDK and disables itself but skips the native `shutdown` call. The native side keeps the
`AlwaysRejectEvent` sampler the game installs for modded runs, so nothing extra is reported. Applied
before the config check and under its own Harmony id. Evidence so far: 3/3 direct launches passed the
freeze point during the bad streak, and 2/2 via the Workshop load path (log: `skipped native Sentry
shutdown`); the unpatched build also passed 4/4 once the streak ended, so this is suggestive, not proof.
Untested: quitting from the menu.
Trade-off: the native crash handler stays loaded, so a real native crash may hang on exit (the
sentry-godot #230/#441 behaviour below) instead of exiting cleanly.

**Status (original, 2026-07, superseded by the workaround above):** Root cause **confirmed** by bisection.
This is a **MegaCrit / game-side bug**. The original advice was not to publish the Workshop item public until
MegaCrit ships a fix; with the workaround the Workshop load path has passed every launch tested since
(see above), so public release no longer waits on that fix. Still tracked upstream: see
`docs/megacrit-workshop-crash-report.md`.

**Symptom:** With the mod installed via **Steam Workshop** and enabled, the game **hard-freezes
(beachball, force-quit required) within ~2s of startup**, around the main menu. The Godot log ends
abruptly with no clean-exit/leak-at-exit sequence, and there is **no** `[STS2 Damage] disabled after
repeated errors` line — i.e. it is a *native* fault, not a caught managed exception.

**Key fact:** The **identical DLL** runs perfectly when installed **manually** in `mods/` (full combat,
thousands of frames) but freezes when loaded via **Steam Workshop**. Same bytes, different load path.

**Root cause (bisected):** The crash is in the **game's own Sentry telemetry**, not our code. A process
`sample` of the frozen game shows the main thread wedged in
`…CFRunLoop observer → game → libsentry.macos.release.dylib → Object::get_instance_id()` while the
`SentryCrash Exception Handler` thread is in `handleExceptions → thread_suspend` — i.e. a native fault
fired and Sentry's crash-report generation hangs walking the Godot object graph. It reproduces with:
- the full mod (Workshop) — freeze;
- a **neutered** build whose `Initialize` just returns (no tick, no Harmony) — freeze;
- a **separate minimal hello-world** mod (Godot + sts2 only, no Harmony, body = one `GD.Print`) — freeze.

Since a do-nothing hello-world still freezes, the trigger is **the mere loading of *any* C# mod via
Steam Workshop**, independent of mod code. (The mod's `try/catch` safety net cannot catch this — a
native access violation isn't a managed exception.)

**To reproduce / re-verify** (macOS): build, drop the DLL into
`steamapps/workshop/content/2868840/<itemid>/STS2_DamageCharts.dll`, subscribe, launch; `sample "Slay
the Spire 2"` while frozen shows the signature above. Manual install in `mods/` does not freeze.

**Confirmed cause (crash stack):** Launching with `--disable-crash-handler` turns the hang into a clean
`SIGABRT` and reveals the fault: sentry-godot's per-frame CFRunLoop observer calls `std::mutex::lock()`,
which throws `std::system_error` (re-entrant/already-held lock) → unhandled → `std::terminate()` →
`abort()`. By default Godot's crash handler and sentry-cocoa's `SentryCrash` handler then deadlock, so
the process **hangs** instead of exiting. Maps to sentry-godot
**[#472](https://github.com/getsentry/sentry-godot/issues/472)** (reentry guard) and
**[#230](https://github.com/getsentry/sentry-godot/issues/230) /
[#441](https://github.com/getsentry/sentry-godot/issues/441)** (hang-on-crash / macOS). Neither the
`--disable-crash-handler` flag nor clearing `SENTRY_DSN` avoids it (the flag converts hang→abort; the
DSN is hardcoded so the env var is ignored).

**Action:** Reported to MegaCrit (see report doc). The fix is bundling sentry-godot **2.0.1**
([#789](https://github.com/getsentry/sentry-godot/pull/789), the reentry guard). A custom v0.108.0 test
build from MegaCrit (2026-07-12) **still reproduced** the freeze, so revisit Workshop publishing only
once a build ships the 2.0.1 upgrade.
