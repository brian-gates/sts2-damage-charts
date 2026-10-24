# Damage Charts — Slay the Spire 2 mod

A standalone damage analytics and incoming-damage forecast mod for [Slay the Spire 2](https://www.megacrit.com/).

- **Incoming-damage forecast** (always on in combat, docked beside your character): the HP you are
  about to lose this turn and what you'll be left at, as a range when something is uncertain. Counts
  enemy intents, damage over time, Infection and self-damage cards, Block and Osty, and end-of-turn Block
  (Plating, Orichalcum, Cloak Clasp); ignores enemies that will die to Poison before they act. Reads
  **Safe** when nothing can hurt you and pulses when the turn can kill you. Hover it for the per-round
  HP-lost history and an itemized list of sources.
- **Combat stats** (hotkey, default **C**): a full-screen breakdown of damage **dealt** and **taken**
  **by source** (cards, powers like Poison or Thorns, relics, orbs, potions), a per-round chart, the
  run so far, potions used, and a scrollable combat log (mouse wheel, trackpad or scrollbar).
- **End-of-combat summary**: a compact version beside the rewards list after each fight, with potions
  used. C hides and shows it until you move on.
- **Run recap**: the same hotkey out of combat (e.g. on the victory/death screen) opens a run-wide recap
  of dealt / taken / healed / block, by source and per fight.
- **Game-native look**: uses the game's own fonts, icons, hover-tip frames, scrollbars and shaders
  (loaded from your install, never bundled). Optional **big-moment effects** reuse the game's own
  damage-number and flash VFX.
- Optional **always-on per-round chart** of dealt vs taken (`show_bars`).
- Single-player and multiplayer (per-player colors). Reads game state directly — no dependency on any
  other mod.

## Requirements

- A copy of **Slay the Spire 2** installed (the build references the game's own assemblies; nothing
  proprietary is bundled or redistributed here).
- The **.NET 9 SDK** to build the mod from source.

## How it works

- **Amounts** come from subscribing to each `Creature.CurrentHpChanged` event (true post-block HP
  loss). Player-creature drops = taken; enemy drops = dealt; heals and block are totaled separately.
- **Source attribution** comes from a Harmony prefix on the central `CreatureCmd.Damage(...)`: card
  hits use `cardSource.Title`, pet hits credit the pet. With `source_hooks` on, a curated list of the
  game's damage-dealing power/relic/orb/potion hooks (found by a static call-graph sweep of `sts2.dll`)
  stamps the source for the hit that follows; otherwise that damage is labeled `Status`/`Other`.
- **Potions used** come from a postfix on `CombatHistory.PotionUsed`.
- **The forecast** reads intents, powers, relics and the hand through reflection each frame, read-only.
- **Startup freeze workaround**: with any mod loaded, the game's Sentry shutdown can hang startup; the
  mod skips that native call (see [KNOWN_ISSUES.md](docs/KNOWN_ISSUES.md)).

## Build & install

The project resolves the game's DLLs (`sts2.dll`, `GodotSharp.dll`, `0Harmony.dll`) at build time
from your STS2 install. Point `STS2GameDir` at that install. The build is the same on every
platform; only the install path and the `mods/` location differ.

### macOS

```bash
GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2"
dotnet build STS2_DamageCharts.csproj -c Release -o out -p:STS2GameDir="$GAME_DIR"
MODS_DIR="$GAME_DIR/SlayTheSpire2.app/Contents/MacOS/mods"
cp out/STS2_DamageCharts.dll "$MODS_DIR/"
cp mod_manifest.json "$MODS_DIR/STS2_DamageCharts.json"
```

### Windows

```powershell
# Adjust to your Steam library; the .csproj defaults to D:\SteamLibrary\... if unset.
$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
dotnet build STS2_DamageCharts.csproj -c Release -o out -p:STS2GameDir="$GameDir"
copy out\STS2_DamageCharts.dll "$GameDir\mods\"
copy mod_manifest.json "$GameDir\mods\STS2_DamageCharts.json"
```

### Linux

```bash
GAME_DIR="$HOME/.steam/steam/steamapps/common/Slay the Spire 2"
dotnet build STS2_DamageCharts.csproj -c Release -o out -p:STS2GameDir="$GAME_DIR"
cp out/STS2_DamageCharts.dll "$GAME_DIR/mods/"
cp mod_manifest.json "$GAME_DIR/mods/STS2_DamageCharts.json"
```

Then launch the game and enable **Damage Charts** in **Settings → Mods** (accept the consent dialog
on first launch). Restart the game after replacing the DLL — mods load at startup.

## Steam Workshop

### Players

Subscribe to **Damage Charts** on the Steam Workshop and it installs automatically — no manual file
copying. Enable it in **Settings → Mods** and restart the game.

### Maintainers (publishing)

Publishing uses MegaCrit's official [`sts2-mod-uploader`](https://github.com/megacrit/sts2-mod-uploader),
which talks to Steam directly — so it runs **locally with the Steam client open and logged in**, never
in CI. The workspace lives in `dist/workshop/` (`workshop.json` + `image.png`); built content is staged
into `dist/workshop/content/` (git-ignored).

```bash
# One-time: download the uploader (osx-arm64) from the sts2-mod-uploader releases page,
# place the binary at ./tools/ModUploader (git-ignored), or set $MODUPLOADER.

# On Apple Silicon the downloaded binary is unsigned and macOS will SIGKILL it ("Killed: 9").
# Ad-hoc sign it once:
codesign --force --sign - tools/ModUploader

# Build, stage content/, and upload (or print the upload command if the CLI isn't found):
./scripts/package-workshop.sh
```

First run **creates** the Workshop item with the visibility set in `workshop.json` (starts `private`)
and writes `dist/workshop/mod_id.txt` — **commit that file** so later runs update the same item.
After the first publish, subscribe in-client and verify the overlay loads in a run, then flip
`visibility` to `public` (in `workshop.json` or on the Workshop page) and re-run the script.

## Config — `STS2_DamageCharts.conf` (next to the DLL in `mods/`)

```json
{
  "enabled": true,
  "hotkey": "c",
  "forecast": true,
  "native_visuals": true
}
```

- `hotkey`: a bare key like `c` (the default) or `f9`, or modifier+key, e.g. `cmd+d`, `shift+d`, `alt+d`, `ctrl+d`.
- `forecast`: set `false` to hide the incoming-damage forecast.
- `forecast_anchor`: `"player"` (default) docks the forecast beside your character; dragging it switches
  to `"free"` and remembers the spot.
- `summary_on_combat_end`: set `false` to skip the end-of-combat summary.
- `run_recap`: set `false` to disable the out-of-combat run recap (the hotkey then only toggles the
  in-combat breakdown).
- `show_bars`: **default `false`.** Set `true` for the always-on per-round dealt/taken chart.
- `ui_scale`: a number (default `1.0`) that scales every panel (text and spacing) on top of the automatic
  resolution scaling. Bump it (e.g. `1.25`) if text reads too small.
- `native_visuals`: **default `true`.** Set `false` for the original flat look.
- `vfx_moments`: **default `false`.** Set `true` for the game's own VFX on a new biggest hit, a fully
  blocked enemy turn, and a new top damage source.
- `source_hooks`: **default `false`.** When `true`, damage-over-time and relic/orb/potion hits are
  attributed by name (Poison, Doom, Thorns, …) instead of the generic `Status`/`Other`, via a curated
  set of patched hooks. Still opt-in while it is verified in game.

Edits to this file are picked up live (within about half a second), no restart needed.

## License

MIT — see [LICENSE](LICENSE).
