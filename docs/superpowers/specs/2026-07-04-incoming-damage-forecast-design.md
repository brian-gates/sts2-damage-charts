# Incoming Damage Forecast — Design

**Date:** 2026-07-04
**Status:** Approved design, pre-implementation

## What this is

A new always-on overlay element for the STS2 Damage Charts mod that previews how much
HP the player is likely to lose this turn, based on enemy intents and other detectable
HP-loss sources. It renders as an **HP-centric threat bar** with the turn's predicted
damage eating into the player's current HP; hovering the bar reveals an itemized
breakdown. Because some effects are conditional or hidden, the estimate is expressed as a
**min–max range**, not a single certain number.

This is additive: it does not change any existing capture/attribution behavior. It reads
game state read-only and shares the mod's existing conventions (reflection-based access,
poll-based mouse/hover, hide-when-a-menu-is-open, never break combat).

## Goals

- Answer "am I going to die / how much will this turn cost me?" at a glance.
- Surface three quantities: **Incoming** (gross threat), **Block** (mitigation), and
  **Est. HP loss** (net, as a range).
- Be honest about uncertainty rather than fabricating a false-precise number.
- Stay compact and match the existing overlay aesthetic.

## Non-goals

- Perfect prediction. Hidden intents, conditional damage, and player choices make some
  loss unknowable; the design flags rather than guesses.
- Predicting multi-turn futures or optimal play. This is a single-turn "what happens if
  the turn resolved from here" forecast.
- Any change to the existing damage-chart / detail / tooltip views' behavior.

## The forecast model

The forecast computes, for the local player, the HP loss expected **if the turn resolved
from the current state**. It is recomputed each `Tick` from live state.

### HP-loss sources (what counts as "incoming")

1. **Enemy attack intents** — each enemy's `NextMove` / `Intents`, filtered to attack
   intents (`SingleAttackIntent`, `MultiAttackIntent`). Damage is the game's own
   already-computed number (post-Strength, post-Vulnerable), read via the intent's
   `Damage`/`Amount`, multiplied by hit count for multi-hit. These are **known** when the
   intent is revealed.
2. **Enemy non-attack HP effects** — non-"attack" enemy moves that still cost the player
   HP, when detectable as HP loss (e.g. HP-drain effects). Included when we can read a
   concrete HP-loss amount; otherwise treated as uncertainty (see below).
3. **Player damage-over-time / statuses** — HP loss already queued on the player that will
   tick this turn regardless of action (Bleed/Burn/Poison-like powers on the player,
   read from `Powers`). **These bypass block.**
4. **Self-damage cards in hand** — cards the player is holding that cost/deal HP when
   played (read from `Hand` / `CardsInHand`). These are **potential** — conditional on the
   player choosing to play them — and contribute only to the range **maximum** ("if
   played"), never to the guaranteed minimum.

### Mitigation (block)

- Player **block** (`Block`/`CurrentBlock`) absorbs **attack** damage only, and per-hit:
  a multi-hit attack chews through block hit-by-hit; block does not reduce DoTs or other
  unblockable HP loss.
- **Mitigated** = the portion of attack damage that block absorbs =
  `min(block, total attack damage)` (with per-hit accounting for multi-hit attacks).
- When block ≥ all attack damage, attacks are fully absorbed and only unblockable sources
  (DoTs, unblockable effects) remain. If those are also zero, the turn reads as **safe**.

### The range (min–max on Est. HP loss)

- **Minimum** = guaranteed loss: revealed attack damage after block + guaranteed DoTs +
  detectable non-attack HP loss. This is the "at least this much" number.
- **Maximum** = minimum + all uncertain contributions:
  - conditional enemy damage upper bounds (where the game exposes them),
  - self-damage cards in hand ("if played"),
  - anything we can bound but not confirm.
- **Hidden / unknown intents** cannot be bounded numerically. They do **not** inflate the
  max with a fake number; instead they raise a **`+?` flag** on the forecast so the player
  knows the estimate is incomplete.
- In the common case (all intents revealed, no conditional effects, no self-damage cards),
  min == max and the forecast collapses to a single number.

## The threat bar (always-on view)

HP-centric: the full bar is the player's current HP; the turn's predicted loss eats into
it from the "damage" end, with block shown as a shield buffer that incoming passes through
first.

```
        Incoming 24   Block 12   Est. loss 12–15
        ┌─shield─┬──────── HP 40 ────────────────┐
Forecast│▓▓▓▓▓▓▓▓│██████████████████░░░░▓▓▓▓▓▓░░░░│
        └────────┴────────────────────────────────┘
          block    safe HP remaining  loss  hatched
                                       (red) =uncertain tail
```

- **Shield buffer** (left): the player's block, rendered as a segment incoming chews
  through before reaching HP.
- **HP track**: width = current HP (max HP may show as a faint track behind).
  - **Green**: HP that survives the turn (current HP − max predicted loss, floored at 0).
  - **Solid red**: guaranteed HP loss (range minimum).
  - **Hatched red tail**: extends from min to max — the uncertain additional loss.
- **Safe state**: when block covers all attacks and no unblockable loss remains, the bar
  reads clearly safe (full green, subtle ✓, no red).
- **Lethal state**: when max predicted loss ≥ current HP, the empty/HP-zero end
  reddens/flashes — the "you can die this turn" signal.
- **Labels**: the three numbers (Incoming / Block / Est. loss range) ride as small text
  above the bar.
- **Placement**: a new standalone always-on mini panel, draggable with position persisted
  as a viewport fraction (same pattern as `DamageChartView`). Hidden whenever a blocking
  UI/menu/map is open (`IsBlockingUiOpen`/`IsMenuOpen`/`IsMapOpen`), same as other views.

## The hover breakdown (itemized ledger)

Hovering anywhere on the threat bar shows a tooltip ledger (poll-based hover via the
existing `UpdateMouse`/`TryGetHover` pattern), listing each contributing source:

```
┌─ Incoming Forecast ──────────┐
│ Slime      2×6    12          │
│ Cultist    buff    —          │
│ Bleed             3  (unblock.)│
│ Offering?        +6  if played │
│ Hidden intent      ?          │
│ ─────────────────             │
│ Incoming 15  −Block 12        │
│ Est. HP loss  3–9             │
└──────────────────────────────┘
```

- One row per enemy (name, hits×damage or move type), per DoT (flagged *unblockable*), per
  self-damage card (flagged *if played*), plus a row noting hidden/unknown intents.
- Footer restates Incoming, Block, and the Est. HP loss range.

## Architecture & integration

Follows the existing mod structure. Everything static; single mod instance.

- **New `DamageForecast` (model/calculator)** — pure, read-only. Given the current combat
  state, returns an immutable forecast snapshot: `{ incoming, mitigated, minLoss, maxLoss,
  hasHidden, currentHp, maxHp, block, items[] }` where `items` is the ledger rows. Reflects
  into game internals (`Enemies`/`Creatures`, per-creature `NextMove`/`Intents`, attack
  intent `Damage`/`Amount`, player `Powers`/`Block`/`CurrentHp`/`MaxHp`, `Hand`) using the
  mod's existing reflection/`AccessTools`/name-matching conventions and helper patterns —
  never hard references where avoidable, so a game rename degrades gracefully.
- **New `DamageForecastView` (Godot view)** — builds native nodes on a dedicated
  high-`Layer` `CanvasLayer`, driven each `Tick` from the forecast snapshot. Renders the
  threat bar + labels; owns its draggable position (persisted via `SaveConfig`). Poll-based
  hover drives the ledger tooltip.
- **`DamageChartsMod.Tick`** — after existing work, compute the forecast (only in combat)
  and drive the new view. Guarded by the same combat/blocking-UI checks and the same
  swallow-all `try/catch` + consecutive-error self-disable as the rest of capture.
- **Config** — a bool to enable/disable the forecast panel (default on) and its persisted
  position, stored in `STS2_DamageCharts.conf` alongside existing settings.
- **Shared helpers** — reuse `UiTheme` (palette/scale, `StsColors` matching),
  `TextHelper.SafeGetText` for any enemy/card titles, and existing blocker/hover helpers.

## Error handling

- The forecast is best-effort and must **never break combat**: all reflection reads are
  wrapped so a null/missing member yields "unknown" (raising the `+?` flag) rather than
  throwing. The view participates in the mod's existing 5-consecutive-error teardown.
- If the forecast can't be computed at all (e.g. combat state unreadable), the panel simply
  hides that frame rather than showing stale or wrong numbers.

## Verification (manual — no test suite)

Per CLAUDE.md, verification is manual: rebuild, copy DLL into `mods/`, restart the game,
observe in combat. Concrete checks:

- Single-attack enemy: Incoming matches the game's intent number; Est. loss = intent −
  block; safe (green) when block ≥ intent.
- Multi-hit attack vs partial block: block absorbs per-hit, remainder eats HP.
- Player with Bleed/Burn: DoT shows in ledger flagged unblockable and is *not* reduced by
  block.
- Self-damage card in hand (e.g. Offering-style): appears only in the range max / "if
  played" line, never in the guaranteed minimum.
- Hidden/unrevealed intent: `+?` flag shows; no fabricated number.
- Lethal turn: bar shows the die-this-turn signal when max loss ≥ current HP.
- Panel hides when a menu/map/modal is open; drags and persists position across restart;
  never spams the log or disables combat on malformed state.
```

## Open implementation questions (defer to plan)

- Exact reflection path from combat state → per-enemy intent objects and the concrete
  attack-intent damage/hit-count accessors (candidates identified: `NextMove`, `Intents`,
  `SingleAttackIntent`/`MultiAttackIntent`, `Damage`/`Amount`).
- Which player powers count as "ticks-this-turn unblockable HP loss" and how to read their
  magnitude generically.
- How to robustly identify "self-damage cards in hand" (card cost/effect inspection) vs.
  hard-coding a list — prefer a generic detectable-HP-cost check.
