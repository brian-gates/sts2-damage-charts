# DPS Meter / Damage Tracker / Combat Recap — Feature Landscape

Research synthesis to prioritize features for an in-combat damage analytics overlay for **Slay the Spire 2**.
Covers both confirmed STS2 comparables and the broader genre lineage (WoW, FFXIV, PoE, MMO log analyzers)
the STS2 mods are explicitly modeled on.

> **Confidence note.** STS2 GitHub repos and one Steam thread were read directly. Nexus Mods pages
> (Skada #33, Damage Meter #47, DPS Meter #681, KitLib #418) were Cloudflare-blocked, so their
> details come from search-engine snippets — high confidence but not source-verified. The broader-genre
> sections are well-sourced. Where a feature is extrapolated from the genre rather than confirmed in an
> STS2 mod, it is flagged.

---

## TL;DR — top features, ranked by how universally valued they are

1. **Per-encounter / per-scope segmentation** (turn / combat / run, with scrollable history) — universal, table stakes.
2. **A clean dealt-damage bar/number** — the irreducible core.
3. **Damage taken** — the next-most-demanded stat after damage dealt.
4. **Click-to-expand per-source / per-ability breakdown + hover tooltips** — what turns a number into a *useful* number.
5. **Block / mitigation tracking** (STS2's analogue to "effective HP / absorbs").
6. **Death recap — "what killed me"** — one of the most-cited reasons people keep a meter on.
7. **End-of-combat / end-of-run summary screen** — the #1 explicit STS2 player request; thin in current STS2 mods.
8. **Draggable / resizable / persisted overlay placement** — expected polish.
9. **Export (JSON/CSV) + shareable reports** — only KitLib does JSON in STS2; CSV/charts/shareable parses are a wide-open gap.
10. **Percentile / leaderboard comparison** — the genre's single most-valued feature (FFLogs/Warcraft Logs), with **no STS2 equivalent yet** — the biggest differentiator opportunity.

---

## Table-stakes features (nearly every tool has these)

| Feature | How common | Exemplars |
|---|---|---|
| **Damage-dealt bar / number** | Universal | Every tool. Details!, Skada, Recount, all STS2 mods |
| **Per-player attribution (multiplayer)** | Universal in MMOs; standard in STS2 co-op | Details!, ACT; STS2 #47, DPS #681, BAIGUANGMEI, freude916, notred27 |
| **Some scope split** (this fight vs cumulative) | Universal | Details! segments; STS2 #47 (combat↔cumulative), #681 (turn/combat/run) |
| **Always-on, real-time updating overlay** | Universal for in-client meters | Details!, Skada, ACT overlays; your mod's always-on chart |
| **Percentage-of-total bars** | Universal | Every bar-style meter |

These are the price of entry. None of them differentiate.

---

## Common / expected features (most good tools have these)

| Feature | How common | Exemplars / notes |
|---|---|---|
| **Damage taken** | Very common; the #2 stat after dealt | Details! "Damage Taken" mode; STS2 #47 "Received Damage" tab, DPS #681 "effective" stats. Players rate this near must-have. |
| **Block / mitigation / absorbs** | Common (genre: absorbs/DTPS) | STS2 #681 (block + *effective* block), Skada #33, #47, KitLib. STS2-specific analogue to absorbs. |
| **Click-to-expand per-source breakdown** | Common in the good tools | Details! per-spell drill-down; STS2 #681 (cards/powers/relics), #47 Card Log, KitLib (by card/source/turn). This is your by-source panel. |
| **Hover tooltips with detail** | Common | Details! per-spell tooltip; Skada #33 structured tooltips; your per-round hover popup. |
| **Multiple stat modes from one capture** | Common (more = more popular) | Details! ~10+ modes is *why it won*. STS2 Skada #33 advertises 17 categories. |
| **Draggable overlay, position persisted** | Common | STS2 #47, #681, BAIGUANGMEI all draggable; your viewport-fraction persistence. |
| **Resizable overlay** | Common | STS2 #47, #681 (remembers size), BAIGUANGMEI (adaptive height). |
| **Compact / expanded / hidden modes** | Common | BAIGUANGMEI (expand/compact/side-hide); your compact chart + hotkey detail view. |
| **Rebindable hotkey to toggle** | Common | STS2 #47 (settings), notred27 (TAB); your configurable hotkey. |
| **Co-op aware (per-player slots)** | Standard in STS2 | All STS2 mods; your local-player resolution. |

---

## Differentiators / power-user features (the standouts)

| Feature | How common | Exemplars / notes |
|---|---|---|
| **Healing / HPS tracking** | Rare in STS2 | KitLib is the *only* STS2 mod tracking heal. Genre: Details! HPS, ACT. Players explicitly asked for healing stats (Steam). |
| **Activity / uptime / energy-efficiency metrics** | Rare | Genre: Details! activity %, resources. STS2: Skada "energy"/"card efficiency"; players asked for "**energy wasted**" — no one surfaces it. Open gap. |
| **rDPS-style assist attribution** (crediting buffs/enablers) | Rare, prestige feature | STS2 Skada #33 (two-phase additive+multiplicative rDPS assist). Genre: FFLogs rDPS/aDPS/nDPS. Hard to do well; signals seriousness. |
| **Overkill tracking** | Uncommon | STS2 Skada #33, notred27. Genre: Details! min/max/overheal analogue. |
| **Max-hit / last-hit highlights** | Uncommon | STS2 BAIGUANGMEI (last-hit, max-hit). Cheap to add, satisfying. |
| **Records / personal bests** | Rare | STS2 Skada #33 "records". |
| **Debuff / status tracking** (Vuln, Weak, etc.) | Rare | STS2 Skada #33 tracks 9 powers. Genre: Warcraft Logs buff/debuff uptime. |
| **Per-turn dealt/taken bar chart (always on)** | Rare in STS2 | Only Skada #33 and KitLib; **your core feature.** #47/#681/BAIGUANGMEI/freude916/notred27 lack it — a genuine differentiator. |
| **Customizable bar textures/fonts/multiple windows** | Genre power-user, rare in STS2 | Details! (multiple restylable windows). Polish, not a draw. |
| **Combat log / cast sequence** | Genre standard, rare in STS2 | Warcraft Logs cast sequences; your scrollable combat log in the detail view is ahead of most STS2 mods here. |

---

## Recap / post-combat-specific features

This category was called out specifically. The genre splits into a **two-layer model**: a live in-client overlay
for at-a-glance feedback (Details! / ACT / your mod) **plus** a deeper post-combat recap or web service
(FFLogs / Warcraft Logs). The recap layer is where the most-valued genre features live.

| Feature | How common | Exemplars / notes |
|---|---|---|
| **End-of-combat summary** (totals, breakdown when the fight ends) | Genre-standard; **thin in STS2** | Details! per-segment summary; Warcraft Logs encounter report. STS2: no mod confirmed to surface a dedicated end-of-fight recap. |
| **End-of-RUN summary screen** | **#1 explicit STS2 request**, not yet built | Steam thread: *"I would LOVE an after-run stat menu"* — wants damage dealt/received, healing, block-used-efficiently, energy-wasted. **No STS2 mod confirmed to do this.** Biggest near-term opportunity. |
| **Death recap** ("what killed me": sequence of hits + heals + HP) | Genre must-have | Warcraft Logs death analysis, Details! advanced death logs, FFLogs deaths tab. STS2 Skada #33 has a "death log". High-value, rare in STS2. |
| **Boss/elite-fight breakdown** | Requested by STS2 players | Steam thread wants per-boss dealt/received/healing. Genre: per-boss segments are universal. |
| **DPS / damage-over-time graph for the fight** | Genre standard | Details! DPS graphs; Warcraft Logs resource/damage graphs. STS2: your per-round bars are the closest analogue. |
| **Shareable report / online parse upload** | Genre killer feature; **absent in STS2** | FFLogs/Warcraft Logs auto-upload. No STS2 mod uploads or shares. |
| **Percentile / parse-color rankings vs global dataset** | The genre's single most-valued feature; **absent in STS2** | FFLogs parse colors (grey→green→blue→purple→orange→pink→gold), Warcraft Logs percentiles. "What's your parse?" is the raid scene's social currency. No STS2 equivalent — the highest-ceiling differentiator, but requires a backend. |
| **What-if / build contribution analysis** | Genre (theorycraft side) | Path of Building: predictive per-source contribution + build comparison. Maps to a "which cards/relics drove your damage this run" recap. |

---

## Notable gaps / frequently-requested-but-rare in STS2

1. **End-of-run stats summary** — explicitly the top player ask; no confirmed STS2 mod delivers it. *(Confirmed via Steam thread.)*
2. **CSV / image-chart export** — only KitLib exports (JSON). CSV and chart-image export are wide open.
3. **Energy-wasted / efficiency metrics** — requested by players; only partially approximated (Skada "energy", "card efficiency"). Nobody surfaces "energy wasted." *(Confirmed request.)*
4. **Death recap** — common and beloved in the genre; in STS2 only Skada #33's "death log" comes close.
5. **Healing tracking** — only KitLib does it among STS2 mods, despite being a standard genre mode and a stated request.
6. **Shareable reports + percentile leaderboards** — the genre's most-valued feature has **no STS2 implementation**. Highest-ceiling, highest-effort differentiator.
7. **Post-run > live preference for competitive stats** — STS2 players noted live multiplayer stats *"could lead to toxicity"* and preferred reveal-after-the-fight. A toggle to defer competitive stats to a recap is a thoughtful, low-cost differentiator. *(Confirmed request.)*

---

## STS2 comparables — feature matrix

| Feature | Skada #33 | DmgMeter #47 | DPS #681 | notred27 | BAIGUANGMEI | freude916 | KitLib #418 |
|---|---|---|---|---|---|---|---|
| Per-turn scope | ✓ (charts) | – | ✓ | – | – | – | ✓ |
| Per-combat | ✓ | ✓ | ✓ | – | ✓ | ✓ | ✓ |
| Per-run | ✓ | ✓ (cumul) | ✓ | ✓ | ✓ | ✓ (save) | ✓ |
| Source breakdown | ✓ | ✓ (cards) | ✓ | – | – | – | ✓ |
| Block tracked | ✓ | ✓ | ✓ | – | – | – | ✓ |
| Heal tracked | – | – | – | – | – | – | ✓ |
| Death log | ✓ | – | – | – | – | – | – |
| Tabs / modes | dashboard+charts | 3 tabs | resize | TAB table | exp/compact/hide | tooltip | pie+rail+MP |
| Draggable | ? | ✓ | ✓ | ~ | ✓ | – | ✓ |
| Resizable | – | ✓ | ✓ | ~ | adaptive | – | – |
| Export | – | – | – | – | – | – | **JSON** |
| Co-op | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |

Note from BAIGUANGMEI's repo (relevant to this codebase's attribution model): reads `DamageResult.UnblockedDamage`,
treats poison from resolved events (not predicted stacks), and **handles Doom via the kill flow using target HP
before death** — the same gotcha addressed by this mod's `DoomKillPrefix`.

---

## Sources

### STS2 mods
- Skada Damage Meter (Nexus #33): https://www.nexusmods.com/slaythespire2/mods/33
- Damage Meter (Nexus #47): https://www.nexusmods.com/slaythespire2/mods/47
- DPS Meter (Nexus #681): https://www.nexusmods.com/slaythespire2/mods/681
- KitLib (Nexus #418): https://www.nexusmods.com/slaythespire2/mods/418
- STS2-KitLib (GitHub): https://github.com/WRXinYue/STS2-KitLib
- notred27/sts2_damage_tracker: https://github.com/notred27/sts2_damage_tracker
- BAIGUANGMEI/STS2-DamageTracker: https://github.com/BAIGUANGMEI/STS2-DamageTracker
- freude916/sts2-multiplayer-damage-stat: https://github.com/freude916/sts2-multiplayer-damage-stat
- Steam discussion (after-run stats request): https://steamcommunity.com/app/2868840/discussions/0/798966028860351067/

### WoW meters
- Skada vs Recount vs Details (Blizzard forums): https://us.forums.blizzard.com/en/wow/t/skada-vs-recount-vs-details/5411
- Recount or Skada or Details? (WoWInterface): https://www.wowinterface.com/forums/showthread.php?t=52204
- Details! Damage Meter Addon Guide (Warcraft Tavern): https://www.warcrafttavern.com/wow/guides/details-damage-meter-addon-guide/
- Details! on CurseForge: https://www.curseforge.com/wow/addons/details

### FFXIV
- Advanced Combat Tracker (official): https://advancedcombattracker.com/
- ACT for FFXIV guide: https://gist.github.com/preyx/a6af7bfa6b3f9617c464ad72b1400620
- FFLogs overview: https://thegamercodex.com/en/final-fantasy-xiv/tools/fflogs
- FFLogs guide (rankings & uploader): https://whatsontech.us/fflogs/
- FFXIV Percentile Plugin (GitHub): https://github.com/Liquidize/FFXIV_PercentilePlugin
- Cactbot (GitHub): https://github.com/xephero/cactbot

### Path of Exile
- The 4 Stages of Path of Building: https://pathofbuilding.net/the-4-stages-of-path-of-building-pob/
- PoE DPS Calculator (PoECalc): https://poecalc.tools/poe-dps-calculator

### MMO log analyzers
- Warcraft Logs — Rankings help: https://www.warcraftlogs.com/help/ranks/
- How to Use Warcraft Logs (Wowhead): https://www.wowhead.com/guide/how-to-use-warcraft-logs-6341
- WarcraftLogs vs WoWAnalyzer vs WowCoach: https://wowcoach.gg/blog/warcraftlogs-vs-wowanalyzer-vs-wowcoach
