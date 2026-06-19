# Damage Charts — Slay the Spire 2 mod

A standalone in-combat damage analytics overlay for [Slay the Spire 2](https://www.megacrit.com/).

- **Always-on compact chart** (top-right): grouped bars per round of damage **dealt** (solid) vs
  **taken** (faded). Per-player colors in multiplayer.
- **Hotkey-toggled detailed breakdown** (default **Cmd+D**): two columns — damage **dealt** and
  **taken** broken down **by source** (each card by name; powers/DoTs like Poison, Doom, Thorns;
  orbs; relics), with totals and percentages, sorted highest-first.
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

## Config — `STS2_DamageCharts.conf` (next to the DLL in `mods/`)

```json
{
  "enabled": true,
  "hotkey": "cmd+d",
  "show_bars": true
}
```

- `hotkey`: modifier+key, e.g. `cmd+d`, `shift+d`, `alt+d`, `ctrl+d`, or a bare key like `f9`.
- `show_bars`: set `false` to hide the always-on bars and use only the hotkey panel.

## License

MIT — see [LICENSE](LICENSE).
