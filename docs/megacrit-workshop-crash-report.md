# Bug report: Steam Workshop-loaded C# mods hard-freeze the game (Sentry crash-handler hang)

**Game:** Slay the Spire 2 v0.107.1 (Major Update 2, Workshop support)
**Platform:** macOS 15.5 (24F74), Apple Silicon (M4 Pro), Metal / Forward+
**AppId:** 2868840
**Severity:** High — any C# mod installed via Steam Workshop hard-freezes the game on startup.

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

## Suspected cause / suggested fix

The game's Sentry integration appears to take a different code path for Workshop-sourced mod assemblies
(vs. local `mods/` assemblies) — e.g. when building the "running modded" Sentry context / registering
the assembly as a debug image — and that path triggers a native fault whose crash-report generation
then hangs walking the Godot scene tree (`Object::get_instance_id`). Disabling Sentry, or skipping
Sentry's Workshop-mod context/debug-image registration, is the likely fix.

## Workaround (for mod authors, until fixed)

Distribute via **manual install** (`mods/`) rather than Steam Workshop. Manual installs are unaffected.
