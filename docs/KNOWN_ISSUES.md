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
