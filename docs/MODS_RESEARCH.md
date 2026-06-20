# STS2 Modding Landscape — Research for an In-Combat Damage Overlay

Research date: 2026-06-20. Sources are linked inline. STS2 is in Early Access, so
specifics (manifest format, API surface) shift between game versions; treat exact
mechanics as a snapshot. Where a mod's internal rendering approach is not documented
publicly, this is called out rather than guessed.

## State of STS2 modding

Slay the Spire 2 is a Godot 4.x / C# (Mono) rewrite of the original Java game, so the
entire modding ecosystem was rebuilt from scratch — none of the StS1 (ModTheSpire /
BaseMod) tooling carries over.

Key facts:

- **There is a built-in, first-party mod loader.** Mods are C# assemblies with an entry
  method marked `[ModInitializer(...)]` in the `MegaCrit.Sts2.Core.Modding` namespace.
- **Harmony ships with the game.** `0Harmony.dll` (HarmonyX, ~2.4.x) is bundled, so mods
  do runtime IL patching with no extra loader (no BepInEx-equivalent needed). This matches
  how your own mod works.
- **Manifest discovery is version-dependent**: `.pck`-based discovery on stable builds,
  `.json` manifest discovery on 0.99+ / beta builds. Mods live in a `mods/` folder.
- **Steam Workshop support landed in ~update 0.107.1** — one-click subscribe/install.
  This is recent and is rapidly growing the casual player-facing catalog.
- **No rich official content API.** MegaCrit gives you the loader + Harmony; everything
  else (content registration, settings UI, theming helpers) is provided by **community
  framework libraries**, not the game.
- **Maturity: early but accelerating.** Nexus Mods has hundreds of entries (mod IDs into
  the 700s), an active GitHub topic, framework libs, a written modding tutorial, and even
  an MCP/agent-tooling sub-scene. It is well past "nothing exists" but the conventions are
  still settling.

Primary hubs:
- Nexus Mods: https://www.nexusmods.com/slaythespire2
- GitHub topic: https://github.com/topics/slay-the-spire-2
- Steam Workshop (in-client; community hub): https://steamcommunity.com/app/2868840
- Wiki modding tutorials: https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2:Modding_Tutorials
- Written tutorial: https://github.com/fresh-milkshake/Modding-Tutorial
  (covers `[ModInitializer]`, Harmony/HarmonyX, manifests, **native UI reuse incl.
  `StsColors` and pulling fonts/panels from the live scene tree, `CanvasLayer` hierarchy**)

## Frameworks & infrastructure

| Project | What it is | Link |
|---|---|---|
| **ModConfig-STS2** | Reflection-injected "Mods" tab in the game's native settings screen; zero-dependency. Offers Toggle, Slider, Dropdown, KeyBind, TextInput, Button, ColorPicker, Header/Separator widgets auto-rendered from metadata. The de-facto settings-UI standard. | https://github.com/xhyrzldf/ModConfig-STS2 |
| **BaseLib-StS2** (Alchyr) | Content-addition standardization lib used as a dependency by other mods. | https://github.com/Alchyr/BaseLib-StS2 |
| **STS2-RitsuLib** | Broad shared framework: content/keyword/epoch registration, **top-bar buttons, card piles, toast messages, "shell themes", runtime UI**, JSON-backed settings pages, Harmony patching w/ diagnostics, API capability gates. | https://github.com/BAKAOLC/STS2-RitsuLib |
| **STS2-KitLib** (WRXinYue) | In-game dev/debug toolkit: console, Harmony analysis, enemy intents, and a **"Combat stats" panel** (live damage/block/heal by card/source/turn, pie-chart sidebar, run totals, JSON export, `dmstats` console cmd). Uses "rail panels." | https://github.com/WRXinYue/STS2-KitLib |
| **ModConfig (Nexus mirror)** | Same framework, player-facing listing. | https://www.nexusmods.com/slaythespire2/mods/27 |
| Mod managers | SlaySP2Manager, sts2-mod-manager (profiles/modpacks, Nexus integration). | https://github.com/topics/slay-the-spire-2 |

Takeaway: settings UI is a **solved, shared problem** (ModConfig). Combat-overlay UI is
**not** — each damage mod rolls its own.

## Closest comparables — damage / stats / analytics overlays

This is a crowded niche. There are at least 6+ damage/stat overlays, which is the single
most important finding: **your mod is entering a competitive category, and they overlap
heavily on features.**

### In-game overlays (the direct competitors)

| Mod | What it tracks / shows | UI approach | Link |
|---|---|---|---|
| **Skada Damage Meter** | WoW-Skada-inspired HUD, **17 stat categories** (damage, block, overkill, rDPS assist, card efficiency, energy, potions, debuffs, death log, records). Per-turn charts, tooltip details, co-op, optional ModConfig, EN/zh. The most feature-dense competitor. | HUD dashboard + per-turn charts + tooltips. Rendering internals not publicly documented (Nexus). | https://www.nexusmods.com/slaythespire2/mods/33 |
| **Damage Meter** | Per-player damage dealt/received, block gained, poison attribution, card usage. **3 tabs** (Meter / Card Log / Received Damage). Solo & co-op. | **Draggable + resizable** overlay with tabs. | https://www.nexusmods.com/slaythespire2/mods/47 |
| **DPS Meter** | Damage, Effective Damage, Block, Effective Block across **Turn / Combat / Run scopes**; per-character **character-colored rows** that **expand to show source breakdowns**. | Row-based meter, expandable rows, character color coding. | https://www.nexusmods.com/slaythespire2/mods/681 |
| **STS2-DamageTracker** (BAIGUANGMEI, OSS) | Multiplayer damage monitor; works even if other players don't have it. | **Native Godot nodes on a `CanvasLayer`.** Modes: expanded / compact / side-hidden restore-tab; **fully LMB-draggable**; adaptive scrolling height as players increase. Hooks via **Harmony** on STS2 + power methods; reads `DamageResult.UnblockedDamage`, poison from resolved events, doom from kill flow using pre-death HP; typed APIs first w/ reflection fallback; players keyed by `RunState.Rng.StringSeed`. **The single best architectural mirror of your mod.** | https://github.com/BAIGUANGMEI/STS2-DamageTracker |
| **sts2_damage_tracker** (notred27, OSS) | Simple damage table: kill counts, damage, overkill. Works in MP without others having it. | **`TAB`-toggled** table. Harmony (0Harmony) patching; C#/Godot 4.5.1 Mono so almost certainly native Godot nodes, but rendering not documented. | https://github.com/notred27/sts2_damage_tracker |
| **KitLib "Combat stats"** | Live damage/block/heal by card/source/turn + **pie-chart sidebar**, run totals, JSON export. | Part of KitLib's "rail panels" debug UI. | https://github.com/WRXinYue/STS2-KitLib |

### Adjacent stat trackers (not in-combat, but overlapping intent)

| Mod | What it does | Link |
|---|---|---|
| **RunStatTracker** | Per-run **time-series graphs**: damage dealt/taken, gold gained/spent, HP, max HP, gold. Full MP. | https://www.nexusmods.com/slaythespire2/mods/399 |
| **MultiplayerStats** | Per-player run stats for co-op. | https://www.nexusmods.com/slaythespire2/mods/41 |
| **SlayTheStats** | Run stat tracking. | https://www.nexusmods.com/slaythespire2/mods/349 |
| **Display The Spire** | Cards-played stats (this Turn / Combat / Run). | https://www.nexusmods.com/slaythespire2/mods/339 |
| **Relic Stats** | Per-relic trigger/value stats. | https://www.nexusmods.com/slaythespire2/mods/327 |

### Out-of-game / companion overlays (different category)

These are **not** Godot in-process overlays — they read the save file and render in a
browser / OBS source, so they are not UI-design comparables but worth knowing as
competitors for player attention.

- **SpireScope** — local-first companion (card/relic/enemy browser, deck analyzer, live
  run tracker usable as an **OBS browser source / overlay mode**). https://github.com/thequantumfalcon/spirescope
- **sts2-advisor** — real-time card/relic grading overlay backed by community win-rate
  data. https://github.com/ebadon16/sts2-advisor
- Knowledge Demon Data Overlay (Nexus 110), Deck Compass (Nexus 627), StsCompanion (Nexus 340).

## Other notable mods (for ecosystem context)

| Mod | Description | Link |
|---|---|---|
| **Minty-Spire-2** | QoL compilation: summed damage display, multi-attack intent consolidation, heal/gold previews, ascension tooltips, reordered rewards. Settings via in-game mod menu. | https://github.com/erasels/Minty-Spire-2 |
| **CustomSkeletonLoader** | Replace Spine skeletal animations w/o touching game files (136+ skeletons). Harmony prefix/postfix with original-as-fallback. | https://www.nexusmods.com/slaythespire2/mods/505 |
| **sts2-RMP-Mods** | Raise MP lobby limit 4→16 from the settings screen. | https://github.com/Rain156/sts2-RMP-Mods |
| **SLS2Mods** (luojiesi) | UndoAndRedo, QuickRestart, UnifiedSavePath, UpgradeAllCards. | https://github.com/luojiesi/SLS2Mods |
| **sts2-custom-mods** (spencerqfox) | Fog of War, Frozen Hand, Neow/Random-Character custom modes, Friend Trading. | https://github.com/spencerqfox/sts2-custom-mods |
| **STS2MCP / STS2-Agent** | Expose in-game state over localhost HTTP + MCP for AI agents. | https://github.com/Gennadiyev/STS2MCP |
| **STS2-Save-Editor** | Web save editor. | https://github.com/topics/slay-the-spire-2 |

## UI / design approaches across the ecosystem — patterns & gaps

What the landscape reveals about how STS2 mods do UI:

1. **Native Godot nodes on a high-layer `CanvasLayer` is the established overlay pattern.**
   The clearest documented case (STS2-DamageTracker) does exactly this, and the official
   tutorial teaches it (`CanvasLayer` hierarchy, building native nodes). This is the same
   path your mod takes. There is **no sign of an immediate-mode GUI / custom `_draw`
   canvas trend** — everyone builds real nodes.

2. **Theme reuse via `StsColors` + pulling fonts/panel styleboxes from the live tree is a
   recognized best practice**, explicitly covered by the tutorial and offered as "shell
   themes" by RitsuLib. Your `UiTheme` (palette matching `StsColors`, capturing
   font/panel-style from the live tree, resolution `Scale()`) is squarely on-pattern —
   and more rigorous than most listings advertise.

3. **Settings UI is standardized; combat overlay UI is not.** ModConfig (8 widget types,
   native settings-tab injection via reflection) means nobody should hand-roll a settings
   panel — but every damage meter hand-rolls its *combat* overlay. There is no shared
   "combat HUD" toolkit, so visual conventions vary per mod.

4. **Common interaction vocabulary across damage overlays:**
   - Draggable overlays (Damage Meter, STS2-DamageTracker) — often **resizable** too.
   - Multiple density modes: expanded / compact / side-hidden-with-restore-tab.
   - Tabs (Damage Meter: Meter / Card Log / Received Damage).
   - Expandable rows for source breakdown (DPS Meter).
   - Character-colored rows for MP attribution (DPS Meter).
   - Hotkey toggle (sts2_damage_tracker = `TAB`).
   - Scopes: **Turn / Combat / Run** (DPS Meter) — a near-universal axis.

5. **Charts/visualization is rare and a differentiator.** Most meters are **text/row
   tables**. Only KitLib (pie-chart sidebar) and RunStatTracker (time-series line graphs)
   show actual graphics. A **per-round bar chart** is uncommon in the in-combat meters.

6. **Hooking conventions match yours.** Harmony patches + "typed APIs first, reflection
   fallback" + reading post-block HP/`DamageResult.UnblockedDamage`, with special-casing
   for poison (resolved events) and doom (pre-death HP from kill flow). STS2-DamageTracker
   independently arrived at the same poison/doom edge-case handling your mod documents —
   strong validation that those are the genuinely hard attribution cases.

7. **Hotkey suppression is a known headache nobody else seems to document solving.** Your
   `SuppressPrefix` / `DeckOpenPrefix` patches to stop a bare key from triggering the
   game's own input handler are a problem the other meters mostly sidestep by using `TAB`
   or click-toggles. This is a genuine differentiator / harder path you've taken.

## Takeaways for a damage overlay mod

- **The niche is crowded.** 6+ in-game damage/stat meters already exist (Skada is the
  feature king with 17 categories; DPS Meter and Damage Meter are polished and
  draggable/resizable/tabbed). Differentiation matters more than feature parity.

- **Your bar-chart visualization is a real differentiator.** Most competitors are
  text/row tables. Per-round dealt/taken **bar charts** + a hover tooltip are uncommon in
  the in-combat space — lean into "visual, glanceable" rather than "exhaustive stat
  table" (Skada owns the exhaustive lane).

- **Your architecture is mainstream-correct, not exotic.** Native nodes on a `CanvasLayer`,
  `StsColors`/live-tree theme capture, Harmony + reflection-fallback, post-block HP capture,
  poison/doom special-casing — all independently match the best OSS competitor and the
  official tutorial. No red flags; you're on the recommended path and arguably more
  defensively coded (5-error self-disable, try/catch everything).

- **Consider adopting two shared conventions to feel native to the ecosystem:**
  1. **ModConfig integration** for settings (Skada and Minty already do this). You
     currently use a hand-rolled `.conf` file; a ModConfig "Mods" tab would match user
     expectations and give you free KeyBind/Toggle/ColorPicker widgets. Optional but it's
     the de-facto standard. https://github.com/xhyrzldf/ModConfig-STS2
  2. **Explicit Turn / Combat / Run scope toggle** — this tri-scope is near-universal and
     users will look for it. (You have per-round; surfacing combat/run rollups would meet
     the convention.)

- **Multiplayer / per-player attribution is table stakes here**, not a bonus —
  essentially every competitor does it (character-colored rows, per-player slots, works
  even if peers lack the mod). Your per-player-slot + `LocalContext.GetMe` design is in
  line; make sure MP is visible in marketing or you'll look behind.

- **Your bare-key hotkey + suppression work is harder than the norm.** Competitors lean
  on `TAB` or click-toggle to dodge input conflicts. Your approach is more flexible
  (configurable combos) but is carrying complexity others avoid — worth keeping robust, or
  documenting as a feature ("rebindable, won't open the deck").

- **Closest thing to read for cross-checking your own code:**
  [STS2-DamageTracker](https://github.com/BAIGUANGMEI/STS2-DamageTracker) (OSS, same
  CanvasLayer + Harmony + poison/doom model) and KitLib's combat-stats panel
  ([STS2-KitLib](https://github.com/WRXinYue/STS2-KitLib)). The
  [modding tutorial](https://github.com/fresh-milkshake/Modding-Tutorial) is the
  authoritative source on native-UI reuse and `StsColors`.

## Honest uncertainty

- Nexus listing pages (Skada, Damage Meter, DPS Meter, RunStatTracker, etc.) **block
  automated fetching (HTTP 403)**, so their UI details here come from search-result
  summaries, not the full descriptions or screenshots. Treat their exact rendering
  internals (node vs. custom draw, theme reuse) as **undocumented/unverified** — only
  STS2-DamageTracker's "native nodes on CanvasLayer" is explicitly confirmed.
- Exact game version semantics (Workshop in 0.107.1, .pck vs .json manifests) come from
  third-party write-ups and may drift as Early Access progresses.
- This is a snapshot of a fast-moving, Early-Access ecosystem; new meters are likely to
  keep appearing.

## Decision: no ModConfig integration (2026-06-20)

We evaluated and rejected adding a ModConfig-STS2 settings tab. The integration would have
been soft (reflection-only, no DLL reference, no-op when ModConfig is absent), but:

- It only benefits the minority of users who install ModConfig, while `.conf` + hot-reload
  already gives everyone full configuration, including a rebindable hotkey.
- It adds a silent-breakage maintenance surface (reflection on ModConfig's type/property names).
- It conflicts with this mod's core identity: standalone, depends on no other mod.

**Compatibility caveat (known, unfixed):** when ModConfig (v0.2.3, Workshop item 3747557003)
is installed alongside this mod, the game logs a `System.InvalidOperationException: Dev console
used before being created` from the game's `NHotkeyManager._UnhandledInput`. That method is the
one our `SuppressPrefix`/`DeckOpenPrefix` hooks patch, so this is an interaction between our
input-suppression hook and ModConfig (which itself uses "zero Harmony"). It appears only in
ModConfig-present runs (3-mod) and never in 2-mod runs. Not investigated/fixed — recorded here
for any future user who runs both mods and sees this in the log.
