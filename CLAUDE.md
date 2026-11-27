# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A standalone damage analytics and incoming-damage forecast mod for **Slay the Spire 2** (a Godot/C#
game). In combat it shows a forecast of HP about to be lost (docked beside the player) and a hotkey-toggled
by-source breakdown; after combat, a summary beside the rewards list; out of combat, a run recap. A small
always-on dealt/taken-per-round chart sits above the forecast (`show_bars`, `bars_style`). With `native_visuals` on (default) every panel uses
the game's own fonts, icons and frames via `Ui/GameAssets.cs`. It reads game state directly and depends on no
other mod.

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

**Requires the .NET 9 SDK.** Unit tests cover the forecast calculator and the pure tooltip/run-aggregate logic (`tests/`, `dotnet test
tests/STS2_DamageCharts.Tests.csproj -p:STS2GameDir=...`); everything else is verified manually: rebuild,
copy the DLL into `mods/`, **restart the game** (mods load at startup), and watch in-game. Never overwrite
the DLL in place while the game runs (it is memory-mapped); stage it and `mv` it into place. `GD.Print("[STS2 Damage] ...")`
lines go to the Godot console/log; the mod self-disables after 5 consecutive per-frame exceptions.

## Architecture

Source layout: `DamageChartsMod.cs` at the root; feature folders `Tracking/`, `Charts/`, `Forecast/`, `Overlay/`,
`Stats/`, `Share/`, `Ui/`, `Common/`. Namespaces follow folders (`STS2_DamageCharts.<Folder>`); `tests/` mirrors them.

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
   - `PowerSourcePrefix` (`source_hooks`, on by default) patched onto the curated `DamageSourceHooks` list of
     damage-dealing power/relic/orb/potion hook methods (`TryApplyPowerSourceHooks`). It reads the model's
     own `Title`/icon and stamps `_activeSource` for the immediately-following `Damage` call. The stamp is only honored if
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
- `DamageTooltipView` — hover popup near the cursor for a chart group: a round of the compact chart or of
  the full view's per-round chart, or a fight of the recap's per-fight chart. It draws a `GroupDetail`
  (`Tracking/GroupDetail.cs`, pure: the model, per-round/per-fight builders and the group hit-test). Per-fight
  detail comes from `RunAggregate.FightDetail`, which keeps each fight's by-source breakdown plus a
  `FightMeta` (floor, encounter name, Elite/Boss, rounds) captured best-effort at combat start.

**Run stats file** (`Stats/RunStatsFile.cs`, pure; I/O in `TryWriteRunStats`): after each resolved combat
(single-player and co-op), the folded fight from `RunAggregate` is added to `<OS.GetUserDataDir()>/damage-charts/runs/<run_id>.json`
(format v1, spec in sts2-saves `docs/specs/2026-11-11-damage-stats-format.md`; the server rejects any key
not in the spec). `run_id` comes from `Stats/RunId.cs` (copied from the Run Library mod) over RunManager's private
`_startTime`, `RunState.Rng.StringSeed` and the players' `NetId`s. The same id keys the recap aggregate
(`RunAggregate.EnsureRun`), and a fight is written only if the run is unchanged at combat end.

**Live fight state** (`Live/`, pure; I/O in `BeginLive`/`ObserveLive`/`EndLive`): while a fight runs, `LiveWriter`
writes `<user dir>/damage-charts/live.json` (temp file + move, only when the round, side or numbers change; read
throttled by `LiveThrottle`) for the Spire Sync app to upload. The mod never makes network calls. Its `fight` is the
index the runs file will give the fight (`RunStatsFile.NextIndex`); "ended" is written only for a resolved fight the
runs file stored under that index (`LiveEnd.Stored`), since the server treats ended as final, and a failed ended
write is retried out of combat. Off with `run_stats_file`.

**Share button** (`Share/ShareButton.cs`; pure logic in `Share/ShareAction.cs` + `Share/AppStatus.cs`): a postfix on
`NGameOverScreen._Ready` (`TryApplyGameOverHook`) records the screen and picks its run id with `ShareRun.Pick`:
only when the screen's RunState is the live run (else no button; never the wrong run). `UpdateShare` in `Tick`
creates the button lazily and shows it while that screen is on top; a prefix on `NClickableControl.HandleMouse*`
keeps the game's buttons from reacting to presses over it. Pressing
it reads Spire Sync's status file (`spire-sync-status.json` in the user data dir, size-capped) and either opens
`spiresync://share/<run_id>` + copies `<server>/runs/<run_id>`, then re-reads the status file once a second
and follows its `share` entry for that run (`ShareAction.Follow`: waiting, shared, failed, 6 min timeout,
older app), or explains (app not running / signed out). No network calls of its own.
**Share fight** is the same class in a second mode (`ShareSpot.Summary`, under the end-of-combat summary via
`DamageDetailView.SummaryFrame`) and a second button beside Share run on the game-over screen. It shares
`RunStatsFile.LastWritten` (the fight the last `AddFight` produced; cleared at combat start/end and on a failed
write), opens `spiresync://share/<run_id>?fight=<index>&floor=<floor>` (app needs `capabilities: ["share_fight"]`),
and copies `<server>/runs/<run_id>/fights/<index>` only when `ShareAction.FollowFight` sees the app confirm it
(polled about once a second, also while hidden, up to 5 minutes).

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
