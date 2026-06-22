# Damage Charts — Slay the Spire 2 mod

A standalone in-combat damage analytics overlay for [Slay the Spire 2](https://www.megacrit.com/).

- **Always-on compact chart** (top-right): grouped bars per round of damage **dealt** (solid) vs
  **taken** (faded). Per-player colors in multiplayer.
- **Hotkey-toggled full-screen breakdown** (default **C**, for "combat stats"): a full-screen
  takeover with two columns — damage **dealt** and **taken** broken down **by source** (each card by
  name; powers/DoTs like Poison, Doom, Thorns; orbs; relics), with totals and percentages, sorted
  highest-first, over a tall scrollable combat log (mouse-wheel or drag the scrollbar).
- **End-of-run recap**: the same hotkey, pressed **out of combat** (e.g. on the run's victory/death
  screen), opens a run-wide recap — total **dealt / taken / healed / block**, the by-source breakdown,
  a per-**fight** chart, and the whole run's combat log. The aggregate resets when a new run begins.
- Single-player and multiplayer. Reads game state directly — no dependency on any other mod.

## Requirements

- A copy of **Slay the Spire 2** installed (the build references the game's own assemblies; nothing
  proprietary is bundled or redistributed here).
- The **.NET 9 SDK** to build the mod from source.

## How it works

- **Amounts** come from subscribing to each `Creature.CurrentHpChanged` event (true post-block HP
  loss). Player-creature drops = taken; enemy drops = dealt; heals ignored.
- **Source attribution** comes from one Harmony Prefix on the central
  `CreatureCmd.Damage(..., IEnumerable<Creature> targets, decimal, ValueProp, Creature? dealer,
  CardModel? cardSource)`. Card damage uses `cardSource.Title`; non-card damage resolves the
  source from the calling type on the stack (`PoisonPower` → "Poison"). One patch covers every
  damage source; unknowns bucket as "Other".

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
  "show_bars": true
}
```

- `hotkey`: a bare key like `c` (the default) or `f9`, or modifier+key, e.g. `cmd+d`, `shift+d`, `alt+d`, `ctrl+d`.
- `show_bars`: set `false` to hide the always-on bars and use only the hotkey panel.
- `run_recap`: set `false` to disable the out-of-combat end-of-run recap (the hotkey then only toggles
  the in-combat breakdown).
- `ui_scale`: a number (default `1.0`) that scales the overlay — the always-on chart and the full-screen
  breakdown/recap (text and spacing) — on top of the automatic resolution scaling. Bump it (e.g. `1.5`)
  if the text reads too small. Applied live, no restart needed.
- `source_hooks`: **default `false`.** When `true`, damage-over-time and relic/orb/potion hits are
  attributed by name (Poison, Doom, Thorns, …) instead of the generic `Status`/`Other`. This requires
  Harmony-patching many game methods, which can intermittently destabilize the game (a hard hang / "Bad
  IL range"), so it ships off. Enable at your own risk; all other stats work regardless.

Edits to this file are picked up live — change the `hotkey` or `show_bars` and it applies within
about half a second, no restart needed.

## License

MIT — see [LICENSE](LICENSE).
