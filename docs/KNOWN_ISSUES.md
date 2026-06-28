# Known issues

## Power-source attribution hooks can deadlock/crash the game (shipped disabled)

**Status:** Root cause **confirmed** via bisection. The hooks now default **off** (`source_hooks`
config, opt-in). This entry tracks re-implementing safe by-name attribution.

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

## Steam Workshop install hard-freezes the game (game-side Sentry bug, NOT our mod)

**Status:** Root cause **confirmed** by bisection. This is a **MegaCrit / game-side bug**, not fixable
in this mod. **Do not publish the Workshop item public**; distribute via manual install until MegaCrit
ships a fix. Tracked upstream — see `docs/megacrit-workshop-crash-report.md`.

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
the process **hangs** instead of exiting. Maps to sentry-godot **#472** (reentry guard) and **#230 /
#441** (hang-on-crash / macOS). Neither the `--disable-crash-handler` flag nor clearing `SENTRY_DSN`
avoids it (the flag converts hang→abort; the DSN is hardcoded so the env var is ignored).

**Action:** Reported to MegaCrit (see report doc). Revisit Workshop publishing once they confirm a fix.
