# Bug report: Steam Workshop-loaded C# mods hard-freeze the game (Sentry crash-handler hang)

**Game:** Slay the Spire 2 — **reproduces on both the v0.107.1 main branch AND the v0.108.0
`public-beta` branch as of 2026-07-03** (fresh live samples for each below). v0.108.0's Steam-mod-load
fixes do not address it. (On the beta build the Sentry release tag is mis-set — it still reports
`release=v0.107.1` despite being buildid 24032229 / `public-beta`; cosmetic, but their beta crash
telemetry is mislabeled.)
**Platform:** macOS 15.5 (24F74), Apple Silicon (M4 Pro), Metal / Forward+
**AppId:** 2868840
**Severity:** High — any C# mod installed via Steam Workshop hard-freezes the game on startup.

**Key point for triage:** the game already ships the guard for this exact scenario —
`MegaCrit.Sts2.Core.Debug.SentryService.DisableGdExtensionIfModded()`, documented as *"Disables
GDExtension Sentry event capture when mods are detected. Called right after ModManager.Initialize…"* —
but it does **not** prevent the deadlock on the Workshop-load path. See the 2026-07-03 update below.

## Summary

A C# mod that loads and runs **perfectly when installed manually** in `mods/` **hard-freezes the game
(beachball, force-quit required) within ~2s of startup** when the *same DLL* is loaded via **Steam
Workshop**. Bisection shows the crash is **independent of mod code** — even a do-nothing hello-world
mod freezes — and the frozen stack is inside the **game's own Sentry** integration, not the mod.

## Reproduction

1. Build any C# mod (`has_pck:false`, `has_dll:true`). Minimal repro: a single
   `[ModInitializer("Initialize")]` whose body is just `GD.Print("hello")`, referencing only
   `GodotSharp.dll` + `sts2.dll` (no Harmony).
2. Install it **manually** into `mods/` and enable it → **runs fine** (full gameplay, thousands of frames).
3. Publish/subscribe the **same DLL** via Steam Workshop (or drop it into
   `steamapps/workshop/content/2868840/<itemid>/`) and enable it.
4. Launch → **hard freeze (beachball) at ~main menu, within ~2s.** Force-quit required.

The only variable is the load path (`mods/` vs `steamapps/workshop/content/...`); the DLL bytes are
identical (verified by sha256).

## Evidence

**Godot log (Workshop load):** mod loads and initializes normally, then the log ends abruptly with no
clean-exit/leak-at-exit sequence:

```
[INFO] Looking for mods to load from Steam Workshop mod <itemid> in .../workshop/content/2868840/<itemid>
[INFO] Loading assembly DLL .../workshop/content/2868840/<itemid>/STS2_DamageCharts.dll
[STS2 HelloTest] HELLO WORLD MINIMAL MOD — Godot+sts2 only, no Harmony, no tick
[INFO]  --- RUNNING MODDED! --- Loaded 1 mods (2 total)
[INFO] [Sentry.NET] Is running modded
... (log stops; no graceful shutdown) ...
```

**`sample` of the frozen process** — the main thread is 100% wedged in Sentry walking the object graph:

```
Thread (main): DispatchQueue_1: com.apple.main-thread
  -[NSApplication run] → _DPSNextEvent → CFRunLoopRunSpecific → __CFRunLoopRun
    → __CFRunLoopDoObservers → <observer callback> → <Slay the Spire 2 frames>
      → libsentry.macos.release.dylib (+0x737e8) → libsentry.macos.release.dylib (+0xcd3b4)
        → Object::get_instance_id() const          # 100% of samples, leaf

Thread: "SentryCrash Exception Handler (Secondary)"
  handleExceptions (libSentry.dylib) → thread_suspend → mach_msg2_trap   # 100% of samples
```

The `SentryCrash` handler is actively handling a native fault (suspending threads), while the main
thread hangs inside Sentry's object-graph capture (`Object::get_instance_id`). No managed/.NET frames
are executing; the CLR threads are idle.

## Bisection (rules out mod code)

| Build (loaded via Workshop) | Result |
|---|---|
| Full mod | freeze |
| Neutered (our assembly; `Initialize` just returns — no tick, no Harmony) | freeze |
| Separate minimal hello-world (Godot + sts2 only; body = one `GD.Print`) | freeze |
| Any of the above installed **manually** in `mods/` instead | **works** |

A do-nothing hello-world freezing under Workshop load — but not under manual load — shows the trigger
is the game's handling of a **Workshop-loaded** mod assembly (Sentry context/registration), not the mod.

## Confirmed cause (crash stack)

Launching with Godot's crash handler disabled (`--disable-crash-handler`) turns the hang into a clean
crash, revealing the actual fault — `EXC_CRASH (SIGABRT)`, `abort() called`, on the main thread:

```
std::__1::mutex::lock()                                    ← throws std::system_error
  → std::__throw_system_error → __cxa_throw
  → std::terminate → abort()
libsentry.macos.release.dylib                 (+0x4a390)
Slay the Spire 2  (sentry-godot integration)  (×5 frames)
__CFRUNLOOP_IS_CALLING_OUT_TO_AN_OBSERVER_CALLBACK_FUNCTION__
__CFRunLoopDoObservers → CFRunLoopRunSpecific → -[NSApplication run]
```

**sentry-godot's per-frame CFRunLoop observer calls `std::mutex::lock()`, which throws
`std::system_error`** (the underlying `pthread_mutex_lock` returns an error — a re-entrant / already-held
lock). The unhandled C++ exception triggers `std::terminate()` → `abort()`.

Two failure modes from the same fault:
- **Default:** Godot's crash handler and sentry-cocoa's `SentryCrash` handler both fire and **deadlock**
  (one suspends the thread the other needs) → the process **hangs (beachball)** instead of exiting.
  Sampling the hang shows the main thread wedged in `libsentry → Object::get_instance_id` and a
  `SentryCrash Exception Handler` thread in `handleExceptions → thread_suspend`.
- **With `--disable-crash-handler`:** no handler conflict → the process **aborts cleanly** (the stack above).

This maps to open sentry-godot issues: **[#472 "Add reentry guard to before_send"](https://github.com/getsentry/sentry-godot/issues/472)** (the re-entrant lock)
and **[#230 "Linux processes hang on crash (not exiting)"](https://github.com/getsentry/sentry-godot/issues/230)** / **[#441 "Crash on macOS (Cocoa)"](https://github.com/getsentry/sentry-godot/issues/441)** (the
dual-handler hang). The Workshop-mod-load path appears to change startup timing/threading enough to
trip the re-entrancy that the local-`mods/` path does not.

### Suggested fix
Add the reentry guard in the sentry-godot run-loop observer / `before_send` path
([#472](https://github.com/getsentry/sentry-godot/issues/472)), and resolve the
dual crash-handler conflict ([#230](https://github.com/getsentry/sentry-godot/issues/230)) so a fault exits cleanly rather than hanging. Neither is fixable in
mod code — a do-nothing Workshop-loaded mod reproduces it.

## Update 2026-07-03 — still reproduces on v0.107.1; the game's own modded-guard doesn't stop it

Re-verified today on the current **v0.107.1 main branch** (Steam Workshop item `<itemid>`). A live
`sample` of the beachballed process shows the **identical fault** as the original report — same dylib
offsets — confirming the root cause is unchanged:

```
Thread (main): com.apple.main-thread
  __CFRunLoopDoObservers → __CFRUNLOOP_IS_CALLING_OUT_TO_AN_OBSERVER_CALLBACK_FUNCTION__
    → <Slay the Spire 2 frames> → libsentry.macos.release.dylib (+0x737e8) → (+0xcd3b4)
      → Object::get_instance_id() const            # leaf, 100% of samples, 0.4% CPU (blocked)

Thread "SentryCrash Exception Handler (Secondary)":
  handleExceptions (libSentry.dylib) → thread_suspend    # 100% of samples
```

i.e. sentry-godot's per-frame run-loop observer is walking the Godot object graph while sentry-cocoa's
`SentryCrash` handler is suspending threads to capture a native fault → mutual deadlock → beachball.

**Also retested on the v0.108.0 `public-beta` branch (same day):** identical signature — main thread
`libsentry.macos +0x737e8 → +0xcd3b4 → Object::get_instance_id` (1337/1337 samples) plus the
`SentryCrash Exception Handler` thread in `handleExceptions → thread_suspend`. So the modding fixes in
v0.108.0 do not resolve this. Full samples: `sts2_hang_sample_20260703_v0.107.1.txt` and
`sts2_hang_sample_20260703_v0.108.0-beta.txt`.

**Independent third-party reproduction (same day):** subscribing to an unrelated author's C# mod —
*"Spire Details - Damage Meter"* (Workshop `3751199711`, ships `Sts2SpireDetails.dll`, no `.pck`) with
our own mod fully unsubscribed — beachballs with the **same** signature. Sample:
`sts2_hang_sample_20260703_third-party-mod.txt`. This confirms the bug is a property of the
game's C#-Workshop load path, not of any one mod.

**New, decisive finding:** the game has a purpose-built guard for modded startup —

> `SentryService.DisableGdExtensionIfModded()` — *"Disables GDExtension Sentry event capture when mods
> are detected. Called right after ModManager.Initialize to prevent mod errors from being reported
> through the GDExtension before AfterGameInit has a chance to shut everything down."*
> (`SentryService.Shutdown()` and `ShouldStayAliveAfterInit(bool)` also exist.)

Despite that guard, the today's Godot log shows the GDExtension sampler **still being armed after the
mod loads**, and Sentry then shutting down — yet the observer deadlock still fires:

```
[Sentry.GDExtension] Initialized: env=playtesters, release=v0.107.1
...
[INFO]  --- RUNNING MODDED! --- Loaded 1 mods (4 total)
[Sentry.GDExtension] should_sample callable set        ← sampler armed AFTER modded
...
[INFO] [Sentry.NET] Is running modded
[INFO] [Sentry.NET] Shutting down because event reporting is disabled.
[Sentry.GDExtension] Shutting down
[INFO] SteamStatsManager: Global stats received ...     ← last line; hang follows
```

Note this differs from the original capture, where the log died at `[Sentry.NET] Is running modded`.
The managed layer now shuts down cleanly and startup proceeds further — but the **GDExtension
(sentry-native) run-loop observer + `SentryCrash` handler** still collide. So `DisableGdExtensionIfModded`
is disabling *event capture* but is not disarming the per-frame run-loop observer / native crash handler
that actually deadlock.

### Refined suggested fix (game-side)
In the modded path (`DisableGdExtensionIfModded` / `AfterGameInit` shutdown), also **remove the
sentry-godot CFRunLoop observer and the `should_sample` callable**, and resolve the sentry-godot ×
sentry-cocoa dual-handler conflict (sentry-godot [#472](https://github.com/getsentry/sentry-godot/issues/472) reentry guard,
[#230](https://github.com/getsentry/sentry-godot/issues/230)/[#441](https://github.com/getsentry/sentry-godot/issues/441) hang-on-crash). The guard
already exists and runs — it just needs to tear down the observer/handler, not only event reporting.

## Update 2026-07-12 — tested MegaCrit's custom v0.108.0 build; still reproduces

A MegaCrit dev (Chaosed0) responded on the mod-uploader issue tracker (2026-07-06) and provided a
custom macOS build of v0.108.0 to try, with the bundled Sentry libraries rebuilt (the intended
functional change being `SentryOptions.before_capture_screenshot` set to a function returning `false`).
The native dylibs in that build are genuinely different from the shipping ones:

| Library | Shipping v0.107.1 | Chaosed0 test build |
|---|---|---|
| `libsentry.macos.release.dylib` | `f15bbbd4…` (3,400,832 B) | `9f4271ee…` (3,447,936 B) |
| `libSentry.dylib` | `672a870a…` (15,961,248 B) | `9e224e14…` (16,055,456 B) |

**Tested via the real Steam Workshop load path** (subscribed item `<itemid>`, DLL staged in
`workshop/content/2868840/<itemid>/`). The log confirms the failing path was exercised:

```
[INFO] Looking for mods to load from Steam Workshop mod <itemid> in .../workshop/content/2868840/<itemid>
[INFO] Loading assembly DLL .../workshop/content/2868840/<itemid>/STS2_DamageCharts.dll
[INFO]  --- RUNNING MODDED! --- Loaded 1 mods (1 total)
[INFO] [Sentry.NET] Is running modded
[INFO] [Sentry.NET] Shutting down because event reporting is disabled.
```

**It still hard-freezes at startup — identical stack, same dylib offsets** as every prior capture
(`sample` of the beachballed process, 2466/2466 samples on the leaf):

```
__CFRUNLOOP_IS_CALLING_OUT_TO_AN_OBSERVER_CALLBACK_FUNCTION__
  → libsentry.macos.release.dylib +0x737e8 → +0xcd3b4 → Object::get_instance_id()
```

Two conclusions from this test:

- **`before_capture_screenshot → false` does not fix it.** The deadlock is the run-loop observer /
  crash-handler *arming*, not the screenshot capture. Confirmed a second way: an **unmodded** run of
  the same build reached the main menu fine but then hung *on quit* with the **same** stack — so the
  fault isn't strictly mod-specific; a Workshop-loaded C# mod just changes startup timing enough to trip
  it reliably at launch.
- **No GDScript option can disable it.** The sentry-godot config API (`SentrySDK.init(func(options)…)`)
  exposes only `before_send` and `before_capture_screenshot` — no `disabled` flag and no crash-handler /
  observer toggle. So a fix has to come from either the native addon or from not initializing it.

### The two shippable fixes (game-side)
1. **Upgrade the addon to sentry-godot `2.0.1`.** That release contains
   [#789](https://github.com/getsentry/sentry-godot/pull/789) — the reentry guard for
   [#472](https://github.com/getsentry/sentry-godot/issues/472) — merged 2026-06-29. The recursive `before_send` re-entry it guards against is exactly the
   `std::mutex::lock()` throw → `std::terminate()` captured earlier under `--disable-crash-handler`.
   This is the real fix and also addresses the on-quit hang above.
2. **If the 2.0.1 upgrade is deferred:** neuter Sentry *before* the native handler/observer arm in
   modded sessions — i.e. **don't call `SentrySDK.init()` at all when a mod is enabled**, gated in
   `SentryInit.gd` on the same modded signal `DisableGdExtensionIfModded` uses. Today's guard runs
   *after* the observer/handler are already installed and only disables event reporting; moving the
   suppression ahead of `init()` prevents the observer from ever registering.

## Workaround (for mod authors, until fixed)

Distribute via **manual install** (`mods/`) rather than Steam Workshop. Manual installs are unaffected.
(We are **not** having the mod force-disable the game's Sentry from `Initialize` — that reaches into the
game's telemetry; the correct fix is game-side, above.)
