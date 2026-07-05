# Incoming Damage Forecast

An always-on overlay that previews how much HP you are likely to lose **this turn** — from
enemy intents, damage-over-time, Osty's absorption, and self-damage/Infection cards — rendered
as an HP-centric "threat bar" with a hover breakdown. Because some effects are conditional or
hidden, the estimate is a **min–max range**, never false-precise.

Part of the standalone Damage Charts mod; it reads game state read-only and never affects combat.

## What you see

A small, always-on, draggable panel (its own `CanvasLayer`, position persisted as a viewport
fraction, hidden whenever a menu/map/modal is open — same conventions as the damage chart).

```
        Incoming Forecast
        In 16   Block 0   Osty 3   Loss 13
        [ shield | ██████ green ██████ ░ red ]
```

- **Three numbers.** `In` = gross incoming threat (pre-mitigation). `Block` = your block.
  `Osty` = Osty's absorption pool (shown only when Osty is up). `Loss` = predicted HP loss,
  shown as a range `min–max` when anything is uncertain, otherwise a single number.
- **The bar is your current HP.** A shield buffer (block + Osty) sits in front; predicted loss
  eats in from the right. **Green** = HP that survives the worst case; **solid red** = guaranteed
  loss (range minimum); **faint/translucent red** = the uncertain tail up to the maximum.
- **Safe / lethal.** When mitigation covers everything and nothing is hidden, it reads
  `Safe ✓` (full green). When max loss ≥ current HP, the panel border reddens — the
  "you can die this turn" signal.
- **Hover** the bar for an itemized ledger: each enemy attack (`N×D`), each DoT (flagged
  *unblockable*), each self-damage card (*if played*), Infection (guaranteed), a hidden-intent
  note, and the `In / Block / Osty / Loss` footer.

## The forecast model

Computed fresh each `Tick` for the local player. Sources of HP loss:

| Source | Blockable? | Timing | Counts toward |
| --- | --- | --- | --- |
| Enemy attack intents | yes (block + Osty) | this turn | min (revealed) |
| Player DoTs (Bleed/Burn/Poison…) | no | this turn | min |
| Infection cards held in hand | no | end of turn if held | min (guaranteed) |
| Self-damage cards ("take/lose … HP") | no | only if played | max only (conditional) |
| Hidden / unreadable intents | — | unknown | raises a `+?` flag, no fake number |

**Mitigation.** Enemy attacks are absorbed by a combined pool of **player block + Osty
(block + HP)** — Osty is a damage sink standing in front of the player; overflow past the pool
reaches your HP. DoTs, Infection, and self-damage bypass this pool. The loss total is
order-independent, so the pool is applied as a single subtraction:

```
attackLoss   = max(0, totalAttackDamage − playerBlock − ostyPool)
minLoss      = attackLoss + guaranteedUnblockable        (DoTs + Infection)
maxLoss      = minLoss + conditional (self-damage-if-played, conditional attacks)
```

Hidden intents can't be bounded numerically, so they never inflate `max`; they only raise the
`+?` flag (and keep the turn from reading as `Safe`).

## Architecture

Four focused files, following the mod's existing patterns (all `static`, single instance):

- **`DamageForecast.cs`** — the pure calculator (`ForecastKind`, `ForecastItem`, `ForecastInputs`,
  `ForecastResult`, `DamageForecast.Compute`). Zero Godot/game dependencies, so it is the one
  unit-tested piece (`tests/DamageForecastTests.cs`, run with `dotnet test`).
- **`ForecastReader.cs`** — reflection-only reader that turns live combat state into a plain
  `ForecastInputs`. Never throws; anything it can't read degrades to "unknown" (hidden flag)
  rather than crashing combat.
- **`DamageForecastView.cs`** — the threat bar (draggable, poll-based mouse/hover).
- **`ForecastLedgerView.cs`** — the hover breakdown tooltip.

`DamageChartsMod.Tick` computes the forecast (in combat, non-blocking) and drives the views.

## Game-internal reflection map (the hard-won part)

All game access is by name via reflection so a game rename degrades gracefully. These are the
actual paths, discovered against the shipped `sts2.xml`/`sts2.dll` and confirmed at runtime — the
"obvious" guesses were mostly wrong, so they are recorded here to save the next person the dig.

- **Player HP / block:** `player.Creature` → `CurrentHp`, `MaxHp`, `Block`.
- **Enemy attack intent & damage:**
  `enemy.Monster` (a `MonsterModel`) → `NextMove` (a `MoveState`) → `Intents` (array of
  `AbstractIntent`; `SingleAttackIntent` / `MultiAttackIntent`). Filter by `IntentType == Attack`
  or type name containing `Attack`. Hit count is **`Repeats`** (not `Hits`/`Amount`).
  - **Damage is NOT a plain field.** `DamageCalc` is a `Func<>` that returns the *base* damage
    (`Decimal`), which under-reports once Strength/Vulnerable apply. Instead call
    **`intent.GetIntentLabel(targets, self)`** where `targets = enemy.CombatState.PlayerCreatures`
    and `self = enemy`; it returns a `LocString` whose formatted text is the exact on-screen number
    (modifiers baked in). Parse the first integer out of it.
- **Osty:** `player.IsOstyAlive`, `player.Osty` (a `Creature`) → `Block` + `CurrentHp` = the
  absorption pool.
- **Player DoTs:** `player.Creature.Powers` — match Bleed/Burn/Poison/Regret/Constrict-style names,
  amount from `Amount`/`Stacks`.
- **Hand:** `player.PlayerCombatState` (not `CombatState`, which is null on the Player) → `Hand`
  (a `CardPile`) → `.Cards`.
- **Card damage / self-harm:** card model `Damage`/`HpLoss`/`CalculatedDamage` are all `0` on the
  in-hand instance — the value only resolves in context. Use
  **`card.GetDescriptionForPile(PileType.Hand)`** to get the description with `{Damage:diff()}`
  resolved to a real number, then classify by text: `take/lose … damage/HP` is self-harm
  (attacks that "deal" are excluded); the number after take/lose is the amount.
- **Infection** (`MegaCrit.Sts2.Core.Models.Cards.Infection`) bites at end of turn while held
  ("At the end of your turn, if this is in your Hand, take N damage"), so it counts as guaranteed
  loss. Added to hand by `PhrogParasite`'s `INFECT_MOVE` (a `StatusIntent`, `IntentType=StatusCard`
  — no damage itself).

## Config (`STS2_DamageCharts.conf`)

- `forecast` (bool, default `true`) — enable the panel.
- `forecast_pos_x_frac` / `forecast_pos_y_frac` — persisted panel position.
- `forecast_debug` (bool, default `false`) — opt-in diagnostics: dumps the real intent / power /
  card member names and per-turn forecast values to the Godot log, for re-confirming the
  reflection map after a game patch.

## Gotchas & known limits

- **Splitting enemies.** When a parent dies and spawns (e.g. Phrog Parasite → wrigglers) there is a
  frame with no living enemies. The mid-combat HUD hide uses a **live** fight-ending check (the
  game's own victory/defeat flags, or player-dead) rather than the sticky "all enemies dead"
  heuristic, so the overlay is not hidden for the rest of the fight. The sticky `_combatResolved`
  flag still drives the end-of-combat summary.
- **Multi-hit** damage is per-hit (from the label) × `Repeats`; verify occasionally against the
  on-screen number.
- **Self-damage attacks** ("Deal X. Take Y.") may pick the wrong number if `Y` isn't a literal in
  the resolved text — the common pure-self-harm cards (Infection, Offering-style) are correct.
- **Hidden intents** are surfaced honestly as `+?`, never guessed.
- The `GetDescriptionForPile` / `GetIntentLabel` calls are the same the game uses to render, so the
  forecast numbers should always match what the player sees on screen.

## Verification

No automated coverage for the game-coupled pieces (per the repo convention) — the calculator has
unit tests (`dotnet test tests/STS2_DamageCharts.Tests.csproj`); the reader and views are verified
manually: rebuild, copy the DLL into `mods/`, restart the game, and watch in combat (enable
`forecast_debug` to compare logged values against the screen).

See also `docs/superpowers/specs/2026-07-04-incoming-damage-forecast-design.md` (design) and
`docs/superpowers/plans/2026-07-04-incoming-damage-forecast.md` (implementation plan).
