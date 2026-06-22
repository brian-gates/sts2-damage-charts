# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A standalone in-combat damage analytics overlay mod for **Slay the Spire 2** (a Godot/C# game). It
renders an always-on per-round dealt/taken bar chart plus a hotkey-toggled by-source breakdown panel.
It reads game state directly and depends on no other mod.

## Build & install

The build references the game's own assemblies (`sts2.dll`, `GodotSharp.dll`, `0Harmony.dll`) resolved
from your STS2 install — nothing proprietary is bundled. You must point `STS2GameDir` at the install;
the `.csproj` derives the platform-specific `data_*` subdir that actually holds the DLLs.

```bash
# macOS
GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2"
dotnet build STS2_DamageCharts.csproj -c Release -o out -p:STS2GameDir="$GAME_DIR"
cp out/STS2_DamageCharts.dll "$GAME_DIR/SlayTheSpire2.app/Contents/MacOS/mods/"
cp mod_manifest.json "$GAME_DIR/SlayTheSpire2.app/Contents/MacOS/mods/STS2_DamageCharts.json"
```

On Windows/Linux the `mods/` dir is directly under `$GAME_DIR`. See README.md for those paths.

For Steam Workshop publishing, `scripts/package-workshop.sh` builds Release, stages
`dist/workshop/content/`, and runs (or prints) MegaCrit's `ModUploader`. It must run locally with the
Steam client logged in — never CI. See the "Steam Workshop" section of README.md.

**Requires the .NET 9 SDK.** There is no test suite — verification is manual: rebuild, copy the DLL into
`mods/`, **restart the game** (mods load at startup), and watch in-game. `GD.Print("[STS2 Damage] ...")`
lines go to the Godot console/log; the mod self-disables after 5 consecutive per-frame exceptions.

## Architecture

Entry point is `DamageChartsMod.Initialize()` (marked `[ModInitializer("Initialize")]`). It connects a
`SceneTree.ProcessFrame` callback (`Tick`), applies Harmony patches, and owns all global state. Everything
is `static` — this is a single mod instance.

**Two-signal capture model** (the central design idea — see the header comment in `DamageChartsMod.cs`):

1. **Amounts** come from subscribing to each `Creature.CurrentHpChanged` event (`OnHpChanged`). This is
   true post-block HP loss: player-creature drops = *taken*, enemy drops = *dealt*, heals ignored.
2. **Source attribution** comes from Harmony prefixes that run *just before* the HP change and stash a
   pending label/dealer/icon per target creature, consumed by `OnHpChanged`:
   - `DamagePrefix` on `CreatureCmd.Damage(...)` (the unique 6-param overload whose 2nd param is
     `IEnumerable<Creature>`, resolved by reflection in `ResolveDamageMethod`). Card hits use
     `cardSource.Title`; pet/summon hits credit the pet; otherwise falls back to the active-source stamp.
   - `PowerSourcePrefix` patched onto *every* overridden hook method of every `PowerModel`/`RelicModel`/
     `OrbModel`/`PotionModel` subclass (`TryApplyPowerSourceHooks`). It reads the power's own `Title`/icon
     and stamps `_activeSource` for the immediately-following `Damage` call. The stamp is only honored if
     "fresh" (`ActiveFresh()`, within ~1 frame) so a stale passive-relic stamp can't mislabel a later hit.
   - `DoomKillPrefix` pre-tags doomed creatures by reference, since Doom kills via an animation-delayed
     `CreatureCmd.Kill` where timing-based attribution would fail.

Unattributed non-card damage buckets as `"Status"`/`"Other"`.

**Hotkey handling** is poll-based (`HandleHotkey` reads `Input.IsPhysicalKeyPressed`), not event-driven.
Because the game's `NHotkeyManager._UnhandledInput` would still open the deck on a bare `D`, two suppress
patches exist (`TryApplySuppressHook`): `SuppressPrefix` swallows the exact combo from the dispatcher, and
`DeckOpenPrefix` no-ops `NDeckViewScreen.ShowScreen` while our modifier is held. Hotkey spec is parsed from
config by `ParseHotkey` (default bare `c`; also accepts combos like `cmd+d` or bare `f9`).

**Data flow:** `DamageTracker` is the thread-safe accumulator — capture writes on the game thread under a
lock; the UI reads immutable `Snapshot()` (per-round bars) / `SourceSnapshot()` (by-source + combat log).
The views never touch tracker internals.

**Views** are pure Godot — they build native nodes on dedicated high-`Layer` `CanvasLayer`s and are driven
each `Tick`. Mouse interaction is poll-based (`UpdateMouse`/`TakeClick`/`TryGetHover`) because GUI input
doesn't reliably reach controls on these layers.
- `DamageChartView` — always-on draggable compact chart (position persisted as a viewport fraction).
- `DamageDetailView` — the hotkey-toggled breakdown. Two modes: a **full-screen takeover** when
  interactive (dim backdrop, wide columns, scrollable log via wheel/drag — wheel arrives through the
  `SuppressPrefix` input hook, not engine GUI callbacks), and a compact click-through **summary** over
  the rewards screen. Sized dynamically from the viewport per mode.
- `DamageTooltipView` — per-round hover popup near the cursor.

**Shared helpers:**
- `UiTheme` — palette matching the game's `StsColors`, font/panel-style capture from the live tree,
  resolution `Scale()`, and (gated by `debug_theme` config) diagnostic dumps of the game's UI nodes/styleboxes.
- `TextHelper.SafeGetText` — resolves the game's `LocString` getters to plain strings and strips rich-text
  tags; used everywhere a game title might be localized or might throw.

## Conventions & gotchas

- **The overlay must never break combat.** Capture hooks and `OnHpChanged` are wrapped in `try/catch` that
  swallow everything; `Tick` counts errors and tears down after 5 in a row rather than spamming Godot's logger.
- **Game internals are reached by reflection / `AccessTools.TypeByName` / name matching**, never hard
  references where avoidable — so the game can rename/move things without a hard crash. New "is a menu open?"
  checks go through `ResolveBlockers` + the `InstanceBool`/`InstanceMemberNotNull` helpers.
- The overlay hides itself when a game menu/modal/overlay is on top (`IsBlockingUiOpen`/`IsMenuOpen`/`IsMapOpen`).
- Config lives in `STS2_DamageCharts.conf` next to the DLL in `mods/` (auto-created on first run). Chart
  position and settings are written back by `SaveConfig` when the chart is dragged.
- Multiplayer: per-player slots throughout; the local player is found via `LocalContext.GetMe(runState)`.
