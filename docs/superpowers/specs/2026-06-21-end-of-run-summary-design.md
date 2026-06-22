# End-of-run summary screen — design (Issue #1)

## Context

Players' #1 explicit STS2 request is an after-run stat menu; no mod delivers it. Before this change the
mod tracked only **dealt** and **taken** for the **current combat** — `DamageTracker` is wiped by
`Reset()` on every `OnCombatStart()` — with no run-end signal and no healing/block capture.

This adds a **run-scoped recap**: per-run **dealt / taken / healed / block** totals, the existing
by-source breakdown, a per-encounter chart, and the full run combat log. It is opened **on demand** via
the existing hotkey when out of combat (e.g. from the run's victory/death screen).

## Decisions

- Capture **healing and block now** (folds in part of #4); shown as **run totals only** — dealt/taken
  keep their by-source columns.
- Recap data is a **run-level aggregate** across all combats.
- The recap chart shows **one bar per encounter** (fight), not per round.
- **On-demand** trigger — no auto takeover. The aggregate persists onto the end screen; the hotkey opens
  the recap as a full-screen takeover, dismissible via the back button.

## Capture model (extends the existing two-signal design)

- **Healing**: the inverse of the existing `Creature.CurrentHpChanged` path. When a player creature's HP
  *increases*, the gain is recorded as a per-player heal total (`OnHpChanged` in `DamageChartsMod.cs`).
- **Block**: `Creature.BlockChanged` is an `Action<int,int>` event mirroring `CurrentHpChanged`. We
  subscribe alongside HP and sum the **positive deltas** for player creatures (actual block applied,
  incl. modifiers). This replaced the originally-planned `CreatureCmd.GainBlock` Harmony hook — the
  event is synchronous, accurate, and needs no patch or `BlockVar`/`Decimal` amount resolution. (The
  async `GainBlock` exposes only the *requested* amount and can't be measured via pre/post deltas.)
- Both are stored on `DamageTracker` as per-player totals (`PlayerSources.HealTotal` / `.BlockTotal`),
  with `AddHealed(slot, amount)` / `AddBlock(slot, amount)`. No by-source maps for heal/block.

## Run aggregation

`RunAggregate.cs` (new; single-threaded, game-thread only, so lock-free):

- `Fold(SourceSnapshot, ChartSnapshot)` — called at `OnCombatEnd` (before the next combat's `Reset()`
  wipes the tracker) when `_tracker.HasData()`. Accumulates per slot: dealt/taken/heal/block totals,
  merged dealt-by-source and taken-by-source maps, one per-encounter row (the combat's dealt/taken
  totals), and the combat's log lines prefixed by a `──── Fight N ────` separator (run-wide cap 2000).
- `Clear()` — called on a run-start edge (`RunManager.Instance.IsInProgress` false→true) so the recap
  never shows a previous run's data.
- `RunSourceSnapshot()` / `RunChartSnapshot()` — emit the **existing** `SourceSnapshot` / `ChartSnapshot`
  types so the current renderer is reused unchanged. The per-encounter chart reuses `ChartRow` with the
  `Round` field carrying the 1-based fight index and one synthetic dealt segment per slot.

## Recap view & trigger

- `DamageDetailView` gains a `RecapMode` flag layered on the existing full-screen takeover. When set it
  adds a **totals band** (Dealt / Taken / Healed / Block, color-coded) above the by-source columns,
  titles the screen "Run Recap", and labels the chart axis "Damage / Fight". The three existing bands
  (columns, chart, log), back button, and wheel-scroll are reused as-is.
- `DamageChartsMod`: a `_recapVisible` flag, toggled by the hotkey **when out of combat and the
  aggregate has data**. The out-of-combat render branch draws the recap and — unlike the in-combat
  takeover — is **not gated by `IsBlockingUiOpen()`**, so it appears over the game's end screen.
- `run_recap` config flag (default true) disables the feature.

## Files

- `DamageTracker.cs` — heal/block totals + adders.
- `RunAggregate.cs` — new run-scope accumulator.
- `DamageChartsMod.cs` — heal capture, block subscription, run aggregate wiring, recap trigger, config.
- `DamageDetailView.cs` — recap totals band + axis label.

## Verification (manual — no test suite)

1. Build & install per `CLAUDE.md`; restart the game.
2. Drive a run through ≥2 combats (play a Defend card → block accrues; heal → healing accrues). The
   per-combat summary still appears between fights.
3. End the run; press the hotkey on the end screen → recap shows four totals, one chart bar per fight,
   by-source columns, and the run log with `Fight N` separators. Back button dismisses.
4. Start a new run → aggregate is cleared.
5. Confirm no error-disable fired in the Godot log.
