# Incoming Damage Forecast Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an always-on HP-centric "threat bar" overlay that previews how much HP the player is likely to lose this turn (enemy attacks, DoTs, self-damage cards), shown as a min–max range, with a hover ledger breakdown.

**Architecture:** A dependency-free pure calculator (`DamageForecast.Compute`) turns a plain `ForecastInputs` into a `ForecastResult` (incoming / mitigated / min–max loss / safe / lethal). A reflection reader (`ForecastReader.Build`) reads live combat state into `ForecastInputs` defensively (name-based reflection, never throws). A Godot view (`DamageForecastView`) renders the threat bar each `Tick`; a small `ForecastLedgerView` renders the hover breakdown. Only the pure calculator is unit-tested; the reader and views follow this repo's manual in-game verification.

**Tech Stack:** C# / .NET 9, Godot (GodotSharp), Harmony (existing, untouched here), xUnit (new, for the pure calculator only).

## Global Constraints

- **Requires the .NET 9 SDK.** Target framework `net9.0`, `LangVersion 12.0`, `Nullable enable` (match `STS2_DamageCharts.csproj`).
- **The overlay must never break combat.** Every reflection read and every render is wrapped in swallow-all `try/catch`; the reader returns partial/`unknown` data rather than throwing. New per-frame work participates in the existing 5-consecutive-error `HardDisable` path (it runs inside the existing `Tick` `try`).
- **Reach game internals by reflection / name matching, never new hard references** where avoidable, so a game rename degrades gracefully (raises the hidden flag) instead of crashing.
- **No test suite for game-coupled code** — the reader and views are verified manually: rebuild → copy DLL into `mods/` → **restart the game** → observe. Only `DamageForecast.cs` (zero Godot/game deps) gets xUnit tests.
- **Build command (macOS):**
  ```bash
  GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2"
  dotnet build STS2_DamageCharts.csproj -c Release -o out -p:STS2GameDir="$GAME_DIR"
  cp out/STS2_DamageCharts.dll "$GAME_DIR/SlayTheSpire2.app/Contents/MacOS/mods/"
  ```
- **Logs** go to the Godot console via `GD.Print("[STS2 Damage] ...")`.
- **Config** lives in `STS2_DamageCharts.conf` next to the DLL; `LoadConfig`/`SaveConfig` in `DamageChartsMod.cs` own it.
- Prefer self-documenting code; avoid explanatory comments except the load-bearing "why" ones this repo already favors.

## File Structure

- **Create `DamageForecast.cs`** (repo root) — pure calculator + POCOs (`ForecastKind`, `ForecastItem`, `ForecastInputs`, `ForecastResult`, `DamageForecast`). **Zero** `using Godot` / `using MegaCrit.*`. Compiled into both the mod and the test assembly.
- **Create `ForecastReader.cs`** (repo root) — `static ForecastReader.Build(CombatState, IReadOnlyList<Player>, int) → ForecastInputs`. All game access via reflection helpers. Never throws.
- **Create `DamageForecastView.cs`** (repo root) — Godot view: threat bar on its own `CanvasLayer`, draggable, persisted position, poll-based mouse + hover, modeled on `DamageChartView`.
- **Create `ForecastLedgerView.cs`** (repo root) — hover tooltip listing each forecast item, modeled on `DamageTooltipView`.
- **Create `tests/STS2_DamageCharts.Tests.csproj`** + **`tests/DamageForecastTests.cs`** — xUnit tests for the pure calculator, linking `../DamageForecast.cs`.
- **Modify `STS2_DamageCharts.csproj`** — exclude `tests/**` from the mod's default `.cs` glob.
- **Modify `DamageChartsMod.cs`** — config keys, view lifecycle in `EnsureViews`, and per-`Tick` forecast update/render wiring.

---

### Task 1: Pure forecast calculator + unit tests

The only unit-tested unit. Pure integer math; block absorbs attacks only (per pooled total), DoTs bypass block, conditional items widen the max.

**Files:**
- Create: `DamageForecast.cs`
- Create: `tests/STS2_DamageCharts.Tests.csproj`
- Create: `tests/DamageForecastTests.cs`
- Modify: `STS2_DamageCharts.csproj`

**Interfaces:**
- Produces (consumed by Tasks 2–5):
  - `enum ForecastKind { Attack, Dot, SelfCard, NonAttackHpLoss, Hidden }`
  - `struct ForecastItem` — ctor `(string label, ForecastKind kind, int hits, int damagePerHit, bool blockable, bool conditional)`; `int Total => Hits * DamagePerHit`.
  - `struct ForecastInputs` — ctor `(int currentHp, int maxHp, int block, bool hasHidden, IReadOnlyList<ForecastItem> items)`.
  - `struct ForecastResult` — fields `Incoming, Mitigated, MinLoss, MaxLoss, Block, CurrentHp, MaxHp` (int), `HasHidden` (bool), `Items` (`IReadOnlyList<ForecastItem>`); props `bool IsSafe`, `bool IsLethal`.
  - `static ForecastResult DamageForecast.Compute(ForecastInputs inp)`.

- [ ] **Step 1: Write `DamageForecast.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace STS2_DamageCharts;

internal enum ForecastKind { Attack, Dot, SelfCard, NonAttackHpLoss, Hidden }

// One contributing source of predicted HP loss. Attacks are Blockable; DoTs / non-attack HP loss /
// self-damage cards bypass block. Conditional items (e.g. self-damage-if-played) count toward the
// range MAXIMUM only, never the guaranteed minimum.
internal readonly struct ForecastItem
{
    public readonly string Label;
    public readonly ForecastKind Kind;
    public readonly int Hits;
    public readonly int DamagePerHit;
    public readonly bool Blockable;
    public readonly bool Conditional;

    public ForecastItem(string label, ForecastKind kind, int hits, int damagePerHit, bool blockable, bool conditional)
    {
        Label = label;
        Kind = kind;
        Hits = hits;
        DamagePerHit = damagePerHit;
        Blockable = blockable;
        Conditional = conditional;
    }

    public int Total => Hits * DamagePerHit;
}

internal readonly struct ForecastInputs
{
    public readonly int CurrentHp;
    public readonly int MaxHp;
    public readonly int Block;
    public readonly bool HasHidden;
    public readonly IReadOnlyList<ForecastItem> Items;

    public ForecastInputs(int currentHp, int maxHp, int block, bool hasHidden, IReadOnlyList<ForecastItem> items)
    {
        CurrentHp = currentHp;
        MaxHp = maxHp;
        Block = block;
        HasHidden = hasHidden;
        Items = items ?? Array.Empty<ForecastItem>();
    }
}

internal readonly struct ForecastResult
{
    public readonly int Incoming;   // guaranteed gross threat (attacks + guaranteed unblockable), pre-block
    public readonly int Mitigated;  // block absorbed vs guaranteed attacks
    public readonly int MinLoss;    // guaranteed HP loss after block
    public readonly int MaxLoss;    // MinLoss + conditional contributions
    public readonly int Block;
    public readonly int CurrentHp;
    public readonly int MaxHp;
    public readonly bool HasHidden;
    public readonly IReadOnlyList<ForecastItem> Items;

    public ForecastResult(int incoming, int mitigated, int minLoss, int maxLoss,
        int block, int currentHp, int maxHp, bool hasHidden, IReadOnlyList<ForecastItem> items)
    {
        Incoming = incoming;
        Mitigated = mitigated;
        MinLoss = minLoss;
        MaxLoss = maxLoss;
        Block = block;
        CurrentHp = currentHp;
        MaxHp = maxHp;
        HasHidden = hasHidden;
        Items = items ?? Array.Empty<ForecastItem>();
    }

    // Safe only when nothing can cost HP AND nothing is hidden (an unknown intent might still hurt).
    public bool IsSafe => MaxLoss <= 0 && !HasHidden;
    public bool IsLethal => CurrentHp > 0 && MaxLoss >= CurrentHp;
}

internal static class DamageForecast
{
    public static ForecastResult Compute(ForecastInputs inp)
    {
        int guaranteedAttack = 0, guaranteedUnblockable = 0, condAttack = 0, condUnblockable = 0;

        var items = inp.Items;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Kind == ForecastKind.Hidden) continue;
            int t = it.Total;
            if (t <= 0) continue;
            if (it.Conditional)
            {
                if (it.Blockable) condAttack += t; else condUnblockable += t;
            }
            else
            {
                if (it.Blockable) guaranteedAttack += t; else guaranteedUnblockable += t;
            }
        }

        int block = Math.Max(0, inp.Block);
        int mitigated = Math.Min(block, guaranteedAttack);
        int guaranteedAttackLoss = guaranteedAttack - mitigated;
        int minLoss = guaranteedAttackLoss + guaranteedUnblockable;

        int remainingBlock = block - mitigated;
        int condAttackLoss = Math.Max(0, condAttack - remainingBlock);
        int maxLoss = minLoss + condAttackLoss + condUnblockable;

        int incoming = guaranteedAttack + guaranteedUnblockable;

        return new ForecastResult(incoming, mitigated, minLoss, maxLoss,
            block, inp.CurrentHp, inp.MaxHp, inp.HasHidden, items);
    }
}
```

- [ ] **Step 2: Exclude `tests/` from the mod build**

In `STS2_DamageCharts.csproj`, add this `ItemGroup` before the closing `</Project>` (the SDK globs every `.cs` under the repo root by default, which would otherwise pull the xUnit test files into the mod and break the build):

```xml
  <ItemGroup>
    <Compile Remove="tests/**/*.cs" />
  </ItemGroup>
```

- [ ] **Step 3: Write the test project file `tests/STS2_DamageCharts.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>12.0</LangVersion>
    <IsPackable>false</IsPackable>
    <RootNamespace>STS2_DamageCharts.Tests</RootNamespace>
  </PropertyGroup>

  <!-- Compile the pure calculator directly (it has no Godot/game refs), so tests need no game DLLs. -->
  <ItemGroup>
    <Compile Include="../DamageForecast.cs" Link="DamageForecast.cs" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Write the failing tests `tests/DamageForecastTests.cs`**

```csharp
using System.Collections.Generic;
using STS2_DamageCharts;
using Xunit;

public class DamageForecastTests
{
    private static ForecastItem Attack(int perHit, int hits = 1) =>
        new("E", ForecastKind.Attack, hits, perHit, blockable: true, conditional: false);
    private static ForecastItem Dot(int amount) =>
        new("Bleed", ForecastKind.Dot, 1, amount, blockable: false, conditional: false);
    private static ForecastItem SelfCard(int amount) =>
        new("Offering", ForecastKind.SelfCard, 1, amount, blockable: false, conditional: true);

    private static ForecastResult Run(int hp, int maxHp, int block, bool hidden, params ForecastItem[] items) =>
        DamageForecast.Compute(new ForecastInputs(hp, maxHp, block, hidden, new List<ForecastItem>(items)));

    [Fact]
    public void SingleAttackNoBlock_LossEqualsDamage()
    {
        var r = Run(40, 50, 0, false, Attack(12));
        Assert.Equal(12, r.Incoming);
        Assert.Equal(0, r.Mitigated);
        Assert.Equal(12, r.MinLoss);
        Assert.Equal(12, r.MaxLoss);
        Assert.False(r.IsSafe);
        Assert.False(r.IsLethal);
    }

    [Fact]
    public void AttackFullyBlocked_IsSafe()
    {
        var r = Run(40, 50, 12, false, Attack(12));
        Assert.Equal(12, r.Mitigated);
        Assert.Equal(0, r.MinLoss);
        Assert.Equal(0, r.MaxLoss);
        Assert.True(r.IsSafe);
    }

    [Fact]
    public void MultiHitPartialBlock_BlockAbsorbsPooledTotal()
    {
        // 2x6 = 12 incoming, 10 block -> 10 absorbed, 2 through.
        var r = Run(40, 50, 10, false, Attack(6, 2));
        Assert.Equal(12, r.Incoming);
        Assert.Equal(10, r.Mitigated);
        Assert.Equal(2, r.MinLoss);
        Assert.Equal(2, r.MaxLoss);
    }

    [Fact]
    public void ExcessBlockDoesNotOverMitigate()
    {
        var r = Run(40, 50, 20, false, Attack(6));
        Assert.Equal(6, r.Mitigated);
        Assert.Equal(0, r.MinLoss);
        Assert.True(r.IsSafe);
    }

    [Fact]
    public void DotBypassesBlock_AndBlocksAllAttacks()
    {
        // 6 attack fully blocked by 10; 3 bleed ignores block.
        var r = Run(40, 50, 10, false, Attack(6), Dot(3));
        Assert.Equal(9, r.Incoming);   // 6 attack + 3 dot
        Assert.Equal(6, r.Mitigated);  // block only absorbs the attack
        Assert.Equal(3, r.MinLoss);    // bleed still lands
        Assert.Equal(3, r.MaxLoss);
        Assert.False(r.IsSafe);
    }

    [Fact]
    public void SelfDamageCard_OnlyWidensMax()
    {
        // 6 attack blocked; Offering (self 6) is conditional -> min 0, max 6.
        var r = Run(40, 50, 10, false, Attack(6), SelfCard(6));
        Assert.Equal(0, r.MinLoss);
        Assert.Equal(6, r.MaxLoss);
        Assert.False(r.IsSafe);
    }

    [Fact]
    public void HiddenIntent_NotSafeEvenAtZeroLoss()
    {
        var r = Run(40, 50, 0, true);
        Assert.Equal(0, r.MinLoss);
        Assert.Equal(0, r.MaxLoss);
        Assert.True(r.HasHidden);
        Assert.False(r.IsSafe);
    }

    [Fact]
    public void Lethal_WhenMaxLossReachesCurrentHp()
    {
        var r = Run(5, 50, 0, false, Attack(8));
        Assert.True(r.IsLethal);
    }

    [Fact]
    public void ConditionalAttack_AbsorbedByLeftoverBlockFirst()
    {
        // guaranteed 4 attack, block 10 -> 4 absorbed, 6 block left.
        // conditional attack 10 -> 6 absorbed by leftover, 4 through -> maxLoss 4.
        var conditionalAttack = new ForecastItem("E2", ForecastKind.Attack, 1, 10, blockable: true, conditional: true);
        var r = Run(40, 50, 10, false, Attack(4), conditionalAttack);
        Assert.Equal(0, r.MinLoss);
        Assert.Equal(4, r.MaxLoss);
    }
}
```

- [ ] **Step 5: Run the tests to verify they fail (calculator not yet compiled into test assembly)**

Run: `dotnet test tests/STS2_DamageCharts.Tests.csproj`
Expected: FAIL — compile error / assertion failures until `DamageForecast.cs` from Step 1 exists. (If Step 1 was written first, they should already pass — that is acceptable; the point is a green suite before moving on.)

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/STS2_DamageCharts.Tests.csproj`
Expected: PASS — `Passed! - Failed: 0`.

- [ ] **Step 7: Verify the mod still builds (tests excluded from the mod glob)**

Run:
```bash
GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2"
dotnet build STS2_DamageCharts.csproj -c Release -o out -p:STS2GameDir="$GAME_DIR"
```
Expected: `Build succeeded` with no reference to xUnit or `tests/`.

- [ ] **Step 8: Commit**

```bash
git add DamageForecast.cs STS2_DamageCharts.csproj tests/
git commit -m "Add pure incoming-damage forecast calculator with unit tests"
```

---

### Task 2: Combat-state reader + Tick wiring (log-only first light)

Read live combat into `ForecastInputs` — this task covers **enemy attack intents + player HP/block + the hidden flag** (DoTs and self-damage cards land in Task 4). Wire it into `Tick` behind a config flag and **log** the result so all reflection is validated before any pixels are drawn.

**Files:**
- Create: `ForecastReader.cs`
- Modify: `DamageChartsMod.cs` (config load/save, `Tick` wiring, `UpdateForecast` helper)

**Interfaces:**
- Consumes: `ForecastInputs`, `ForecastItem`, `ForecastKind`, `DamageForecast.Compute`, `ForecastResult` (Task 1); `CombatState.Enemies`, `Player.Creature`, `Creature.IsAlive`, `Creature.Monster` (existing game refs used in `DamageChartsMod.cs`).
- Produces: `static ForecastInputs ForecastReader.Build(CombatState cs, IReadOnlyList<Player> players, int localSlot)`.

- [ ] **Step 1: Write `ForecastReader.cs`**

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace STS2_DamageCharts;

// Reads live combat state into a plain ForecastInputs. Every game access is reflection-by-name so a
// game rename degrades to "unknown" (hidden flag) rather than crashing combat. Never throws.
internal static class ForecastReader
{
    public static ForecastInputs Build(CombatState cs, IReadOnlyList<Player> players, int localSlot)
    {
        var items = new List<ForecastItem>();
        int hp = 0, maxHp = 0, block = 0;
        bool hasHidden = false;

        try
        {
            Creature? me = (localSlot >= 0 && localSlot < players.Count) ? players[localSlot].Creature : null;
            if (me != null)
            {
                hp = GetInt(me, "CurrentHp", "Hp", "Health");
                maxHp = GetInt(me, "MaxHp", "MaxHealth");
                block = GetInt(me, "Block", "CurrentBlock");
            }
        }
        catch { }

        try
        {
            foreach (var enemy in cs.Enemies)
            {
                if (enemy == null) continue;
                try { if (!enemy.IsAlive) continue; } catch { }
                if (!TryReadEnemy(enemy, items)) hasHidden = true;
            }
        }
        catch { }

        return new ForecastInputs(hp, maxHp, block, hasHidden, items);
    }

    // Returns true if this enemy's intent was concretely readable (attack or a known non-attack move);
    // false when the intent is hidden/unreadable, so the caller raises the hidden flag.
    private static bool TryReadEnemy(Creature enemy, List<ForecastItem> items)
    {
        var intents = ResolveIntents(enemy);
        if (intents.Count == 0) return false;

        string name = SafeName(enemy);
        foreach (var intent in intents)
        {
            string tn = intent.GetType().Name;
            if (Contains(tn, "Hidden") || Contains(tn, "Unknown")) return false;
            if (!Contains(tn, "Attack")) continue; // buff/defend/etc: known, no HP threat

            int perHit = GetInt(intent, "Damage", "BaseDamage", "Amount");
            int hits = GetInt(intent, "Hits", "Times", "Count", "AttackCount");
            if (hits <= 0) hits = 1;
            if (perHit <= 0) continue; // attack with no readable number → treat as no threat, not hidden
            items.Add(new ForecastItem(name, ForecastKind.Attack, hits, perHit, blockable: true, conditional: false));
        }
        return true;
    }

    private static List<object> ResolveIntents(Creature enemy)
    {
        var list = new List<object>();
        object? single = GetProp(enemy, "NextMove") ?? GetProp(enemy, "Intent") ?? GetProp(enemy, "CurrentMove");
        if (single != null) { list.Add(single); return list; }
        if (GetProp(enemy, "Intents") is IEnumerable en)
            foreach (var i in en) if (i != null) list.Add(i);
        return list;
    }

    private static string SafeName(Creature enemy)
    {
        try { return TextHelper.SafeGetText(() => enemy.Monster!.Title) ?? "Enemy"; }
        catch { return "Enemy"; }
    }

    private static bool Contains(string s, string sub) =>
        s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;

    private static object? GetProp(object obj, string name)
    {
        try
        {
            var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p.GetValue(obj);
            var f = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return f?.GetValue(obj);
        }
        catch { return null; }
    }

    private static int GetInt(object obj, params string[] names)
    {
        foreach (var n in names)
        {
            var v = GetProp(obj, n);
            if (v is int i) return i;
            if (v != null && int.TryParse(v.ToString(), out int p)) return p;
        }
        return 0;
    }
}
```

- [ ] **Step 2: Add the config field and load/save wiring in `DamageChartsMod.cs`**

Add a field near the other view/config flags (after `private static bool _showBars = true;` at line 37):

```csharp
    private static bool _showForecast = true;
    private static float? _fcPosXFrac;
    private static float? _fcPosYFrac;
    private static string _lastForecastLog = "";
```

In `LoadConfig`, after the `show_bars` line (line 137), add:

```csharp
            if (root.TryGetProperty("forecast", out var fc) && fc.ValueKind == JsonValueKind.False) _showForecast = false;
            if (root.TryGetProperty("forecast_pos_x_frac", out var fpx) && fpx.TryGetSingle(out var ffx)) _fcPosXFrac = ffx;
            if (root.TryGetProperty("forecast_pos_y_frac", out var fpy) && fpy.TryGetSingle(out var ffy)) _fcPosYFrac = ffy;
```

In `SaveConfig`, add these entries to the `dict` initializer (after the `show_bars` entry, line 166):

```csharp
                ["forecast"] = _showForecast,
                ["forecast_pos_x_frac"] = _fcForecastPosRounded().X,
                ["forecast_pos_y_frac"] = _fcForecastPosRounded().Y,
```

And add this helper method next to `SaveConfig` (returns the live forecast position, or the last-saved one if the view isn't up yet):

```csharp
    private static Vector2 _fcForecastPosRounded()
    {
        var p = _forecast != null ? _forecast.PositionFraction : new Vector2(_fcPosXFrac ?? 0f, _fcPosYFrac ?? 0f);
        return new Vector2((float)Math.Round(p.X, 4), (float)Math.Round(p.Y, 4));
    }
```

> Note: `_forecast` is declared in Task 3. For Task 2, temporarily declare it as `private static DamageForecastView? _forecast;` alongside the other view fields (line 56-58) — the type is created in Task 3 but the field can exist now; if you prefer to keep Task 2 compiling standalone, have `_fcForecastPosRounded()` read only `_fcPosXFrac/_fcPosYFrac` and add the `_forecast` branch in Task 3. Either way the calculator/reader wiring below does not depend on the view.

- [ ] **Step 3: Wire `UpdateForecast` into `Tick`**

In `Tick`, inside the non-blocking `else` branch that renders the bars (the block at lines 559-572, right after the closing `}` of the `if (_showBars && _bars != null) { ... }`), add:

```csharp
                        if (_showForecast) UpdateForecast(cs);
```

Add the `UpdateForecast` method (log-only in this task; Task 3 makes it drive the view):

```csharp
    private static void UpdateForecast(CombatState cs)
    {
        try
        {
            var inputs = ForecastReader.Build(cs, _players, _localSlot);
            var r = DamageForecast.Compute(inputs);
            string line = $"in={r.Incoming} block={r.Block} loss={r.MinLoss}-{r.MaxLoss} " +
                          $"hidden={r.HasHidden} hp={r.CurrentHp}/{r.MaxHp} items={inputs.Items.Count}";
            if (line != _lastForecastLog) { _lastForecastLog = line; GD.Print($"[STS2 Damage] forecast {line}"); }
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] forecast error: {ex.Message}"); }
    }
```

- [ ] **Step 4: Build and install**

Run:
```bash
GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2"
dotnet build STS2_DamageCharts.csproj -c Release -o out -p:STS2GameDir="$GAME_DIR" \
 && cp out/STS2_DamageCharts.dll "$GAME_DIR/SlayTheSpire2.app/Contents/MacOS/mods/"
```
Expected: `Build succeeded`.

- [ ] **Step 5: Manual verification — confirm the reflected numbers are real**

Restart the game, enter a combat, and watch the Godot log for `[STS2 Damage] forecast ...` lines.

Verify against what you see on screen:
- A single-attack enemy: `in=` matches the game's intent number; `hp=`/`block=` match the player.
- Play a defend/gain block: `block=` and `loss=` update correctly (loss drops as block rises).
- If `in=0` while an enemy clearly shows an attack intent, or `hidden=true` unexpectedly, the reflected member names are wrong. Find the real names and adjust the candidate lists in `ForecastReader` (`GetInt(intent, ...)`, `ResolveIntents`, HP/block `GetInt`):
  ```bash
  GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2"
  XML="$GAME_DIR/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64/sts2.xml"
  DLL="${XML%.xml}.dll"
  # Attack-intent damage/hit accessors:
  strings "$DLL" | grep -E 'AttackIntent|get_(Damage|BaseDamage|Amount|Hits|Times|Count)$' | sort -u
  # Creature HP/block/next-move accessors:
  strings "$DLL" | grep -E 'get_(CurrentHp|MaxHp|Block|CurrentBlock|NextMove|Intents)$' | sort -u
  ```
  Re-run Step 4 after edits until the logged numbers match the screen.

- [ ] **Step 6: Commit**

```bash
git add ForecastReader.cs DamageChartsMod.cs
git commit -m "Read enemy attack intents + player HP/block into forecast (log-only)"
```

---

### Task 3: Threat bar view

Render the forecast as an HP-centric threat bar on its own `CanvasLayer`, driven each `Tick`. Block is a shield buffer; the HP track shows green (survives) / solid red (guaranteed loss) / translucent red (uncertain up to max); safe and lethal states are visually distinct.

**Files:**
- Create: `DamageForecastView.cs`
- Modify: `DamageChartsMod.cs` (`EnsureViews`, `UpdateForecast`, teardown)

**Interfaces:**
- Consumes: `ForecastResult` (Task 1); `UiTheme` (existing).
- Produces:
  - `class DamageForecastView` ctor `(Node root, Vector2? savedPosFrac)`.
  - `bool IsValid()`, `void Hide()`, `void Dispose()`, `Vector2 PositionFraction`, `bool IsDragging`, `float UiScaleMul`.
  - `void Render(ForecastResult r)`.
  - `bool UpdateMouse(bool leftDown)` → returns true when a drag just finished (caller persists).
  - `ForecastResult? LastResult` and `bool IsHovered()` (used by Task 5).

- [ ] **Step 1: Write `DamageForecastView.cs`**

```csharp
using System;
using Godot;

namespace STS2_DamageCharts;

// Always-on, draggable HP-centric threat bar. Left shield segment = block (consumed portion darker);
// HP track = current HP with predicted loss eating in from the right (solid red = guaranteed min,
// translucent red = uncertain up to max). Reads clearly safe when nothing can cost HP, and flags
// lethal when max loss >= current HP. Native nodes only; poll-based mouse from the tick.
internal sealed class DamageForecastView
{
    private const float BaseWidth = 300f;
    private const float BaseHeight = 76f;
    private const float BaseHeaderH = 22f;
    private const float BaseBarH = 22f;
    private const float BaseMargin = 14f;

    private static readonly Color Green = new(0.42f, 0.83f, 0.36f);
    private static readonly Color Shield = new(0.55f, 0.72f, 1f, 0.55f);
    private static readonly Color ShieldUsed = new(0.30f, 0.45f, 0.75f, 0.85f);
    private static readonly Color LossMin = UiTheme.Red;
    private static readonly Color LossMax = new(UiTheme.Red.R, UiTheme.Red.G, UiTheme.Red.B, 0.40f);
    private static readonly Color TrackBg = new(0f, 0f, 0f, 0.35f);

    private readonly CanvasLayer _layer;
    private readonly Control _container;
    private readonly Panel _bg;
    private readonly Control _barLayer;
    private readonly Label _title;
    private readonly Label _stats;
    private readonly ColorRect _track;
    private readonly ColorRect _shield;
    private readonly ColorRect _shieldUsed;
    private readonly ColorRect _green;
    private readonly ColorRect _lossMin;
    private readonly ColorRect _lossMax;

    public float UiScaleMul = 1f;
    public ForecastResult? LastResult { get; private set; }

    private Vector2 _posFrac;
    private bool _posFracInit;
    private bool _dragging;
    private bool _leftLast;
    private Vector2 _dragOffset;
    private Vector2 _pressStart;

    private Vector2 _vp = new(1920, 1080);
    private float _w = BaseWidth, _h = BaseHeight, _headerH = BaseHeaderH;
    private float _topInset;

    public DamageForecastView(Node root, Vector2? savedPosFrac)
    {
        _layer = new CanvasLayer { Layer = 129, Name = "Sts2DamageChartsForecast" };
        root.AddChild(_layer);

        _container = new Control { Name = "Forecast", MouseFilter = Control.MouseFilterEnum.Ignore };
        _layer.AddChild(_container);

        _bg = UiTheme.MakePanel(0.5f, 1f);
        _bg.MouseFilter = Control.MouseFilterEnum.Stop;
        _bg.SetPosition(Vector2.Zero);
        _container.AddChild(_bg);

        _barLayer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _barLayer.SetPosition(Vector2.Zero);
        _container.AddChild(_barLayer);

        _track = NewRect(TrackBg);
        _shield = NewRect(Shield);
        _shieldUsed = NewRect(ShieldUsed);
        _green = NewRect(Green);
        _lossMax = NewRect(LossMax);   // painted before min so min sits on top
        _lossMin = NewRect(LossMin);

        _title = UiTheme.MakeLabel(12, new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.85f));
        _title.Text = "Incoming Forecast";
        _container.AddChild(_title);

        _stats = UiTheme.MakeLabel(12, UiTheme.Cream);
        _container.AddChild(_stats);

        if (savedPosFrac.HasValue) { _posFrac = savedPosFrac.Value; _posFracInit = true; }
    }

    private ColorRect NewRect(Color c)
    {
        var r = new ColorRect { Color = c, MouseFilter = Control.MouseFilterEnum.Ignore };
        _barLayer.AddChild(r);
        return r;
    }

    public bool IsValid() => GodotObject.IsInstanceValid(_layer);
    public void Hide() { if (IsValid()) _layer.Visible = false; }
    public void Dispose() { if (IsValid()) _layer.QueueFree(); }
    public Vector2 PositionFraction => _posFrac;
    public bool IsDragging => _dragging;

    public bool IsHovered()
    {
        if (!IsValid() || _dragging) return false;
        try
        {
            Vector2 local = _container.GetLocalMousePosition();
            return local.X >= 0 && local.X <= _w && local.Y >= 0 && local.Y <= _h;
        }
        catch { return false; }
    }

    private static float TopInset(Vector2 vp) => Math.Max(48f, vp.Y * 0.09f);

    public bool UpdateMouse(bool leftDown)
    {
        if (!IsValid()) return false;
        bool savedNow = false;
        try
        {
            Vector2 global = _container.GetGlobalMousePosition();
            Vector2 local = _container.GetLocalMousePosition();
            bool overHeader = local.X >= 0 && local.X <= _w && local.Y >= 0 && local.Y <= _headerH;

            if (leftDown && !_leftLast)
            {
                _pressStart = global;
                if (overHeader) { _dragging = true; _dragOffset = global - new Vector2(_posFrac.X * _vp.X, _posFrac.Y * _vp.Y); }
            }
            else if (leftDown && _dragging)
            {
                Vector2 abs = global - _dragOffset;
                float yMin = _vp.Y > 0 ? _topInset / _vp.Y : 0f;
                float x = _vp.X > 0 ? Math.Clamp(abs.X / _vp.X, 0f, Math.Max(0f, 1f - _w / _vp.X)) : 0f;
                float y = _vp.Y > 0 ? Math.Clamp(abs.Y / _vp.Y, yMin, Math.Max(yMin, 1f - _h / _vp.Y)) : 0f;
                _posFrac = new Vector2(x, y);
            }
            else if (!leftDown && _leftLast)
            {
                bool moved = (global - _pressStart).Length() > 5f;
                if (_dragging) { _dragging = false; if (moved) savedNow = true; }
            }
            _leftLast = leftDown;
        }
        catch { }
        return savedNow;
    }

    public void Render(ForecastResult r)
    {
        if (!IsValid()) return;
        try
        {
            _layer.Visible = true;
            LastResult = r;

            _vp = _container.GetViewportRect().Size;
            float sc = UiTheme.Scale(_vp) * Math.Clamp(UiScaleMul, 0.5f, 4f);
            _w = BaseWidth * sc; _h = BaseHeight * sc; _headerH = BaseHeaderH * sc;
            float margin = BaseMargin * sc;

            _container.SetSize(new Vector2(_w, _h));
            _bg.SetSize(new Vector2(_w, _h));
            _barLayer.SetSize(new Vector2(_w, _h));
            UiTheme.RestylePanel(_bg, 0.5f, sc);

            float inset = TopInset(_vp);
            _topInset = inset;
            if (!_posFracInit)
            {
                _posFrac = new Vector2(_vp.X > 0 ? margin / _vp.X : 0.02f,
                                       _vp.Y > 0 ? Math.Max(margin, inset) / _vp.Y : 0.02f);
                _posFracInit = true;
            }
            float px = Math.Clamp(_posFrac.X * _vp.X, 0f, Math.Max(0f, _vp.X - _w));
            float py = Math.Clamp(_posFrac.Y * _vp.Y, inset, Math.Max(inset, _vp.Y - _h));
            _container.SetPosition(new Vector2(px, py));

            UiTheme.Apply(_title, F(13, sc), new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.85f));
            _title.SetPosition(new Vector2(8 * sc, 4 * sc));

            string lossStr = r.MinLoss == r.MaxLoss ? r.MinLoss.ToString() : $"{r.MinLoss}–{r.MaxLoss}";
            string hidden = r.HasHidden ? "  +?" : "";
            string statsText = r.IsSafe
                ? $"Safe ✓   Block {r.Block}{hidden}"
                : $"In {r.Incoming}   Block {r.Block}   Loss {lossStr}{hidden}";
            var statColor = r.IsLethal ? UiTheme.Red : (r.IsSafe ? Green : UiTheme.Cream);
            UiTheme.Apply(_stats, F(13, sc), statColor);
            _stats.Text = statsText;
            _stats.SetPosition(new Vector2(8 * sc, _headerH + 2 * sc));

            LayoutBar(r, sc);

            // Lethal: red border tint on the panel to signal you can die this turn.
            _bg.SelfModulate = r.IsLethal ? new Color(1f, 0.5f, 0.5f) : Colors.White;
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] forecast render error: {ex.Message}"); }
    }

    private void LayoutBar(ForecastResult r, float sc)
    {
        float barH = BaseBarH * sc;
        float barY = _h - barH - 8 * sc;
        float x0 = 8 * sc;
        float barW = _w - 16 * sc;

        int block = Math.Max(0, r.Block);
        int hp = Math.Max(0, r.CurrentHp);
        int denom = Math.Max(1, block + hp);
        float shieldW = barW * block / denom;
        float hpW = barW * hp / denom;
        float hpX = x0 + shieldW;

        // Track background under the whole bar.
        Place(_track, x0, barY, barW, barH);

        // Shield buffer (block); consumed portion (mitigated) rendered darker on its left.
        Place(_shield, x0, barY, shieldW, barH);
        float usedW = block > 0 ? shieldW * Math.Min(block, r.Mitigated) / block : 0f;
        Place(_shieldUsed, x0, barY, usedW, barH);

        // HP track: loss eats in from the right. minLoss (solid) and up-to-max (translucent) clamp to HP.
        int minLossHp = Math.Clamp(r.MinLoss, 0, hp);
        int maxLossHp = Math.Clamp(r.MaxLoss, 0, hp);
        float wPerHp = hp > 0 ? hpW / hp : 0f;

        float greenW = (hp - maxLossHp) * wPerHp;
        Place(_green, hpX, barY, greenW, barH);

        float maxLossW = maxLossHp * wPerHp;
        Place(_lossMax, hpX + hpW - maxLossW, barY, maxLossW, barH);

        float minLossW = minLossHp * wPerHp;
        Place(_lossMin, hpX + hpW - minLossW, barY, minLossW, barH);
    }

    private static void Place(ColorRect r, float x, float y, float w, float h)
    {
        bool vis = w > 0.5f && h > 0.5f;
        r.Visible = vis;
        if (!vis) return;
        r.SetPosition(new Vector2(x, y));
        r.SetSize(new Vector2(w, h));
    }

    private static int F(int baseSize, float sc) => Math.Max(8, (int)Math.Round(baseSize * sc));
}
```

- [ ] **Step 2: Add `_forecast` to view lifecycle in `DamageChartsMod.cs`**

Confirm the field exists (added in Task 2) alongside the other views (lines 56-58):

```csharp
    private static DamageForecastView? _forecast;
```

In `EnsureViews`, after the `_bars` creation line (line 704), add:

```csharp
            Vector2? fcPos = (_fcPosXFrac.HasValue && _fcPosYFrac.HasValue) ? new Vector2(_fcPosXFrac.Value, _fcPosYFrac.Value) : (Vector2?)null;
            if (_forecast == null || !_forecast.IsValid()) _forecast = new DamageForecastView(root, fcPos);
            if (_forecast != null) _forecast.UiScaleMul = _uiScale;
```

- [ ] **Step 3: Make `UpdateForecast` drive the view instead of logging**

Replace the `UpdateForecast` body from Task 2 with:

```csharp
    private static void UpdateForecast(CombatState cs)
    {
        try
        {
            if (_forecast == null) return;
            var inputs = ForecastReader.Build(cs, _players, _localSlot);
            var r = DamageForecast.Compute(inputs);
            _forecast.Render(r);
            if (_forecast.UpdateMouse(Input.IsMouseButtonPressed(MouseButton.Left))) SaveConfig();
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] forecast error: {ex.Message}"); }
    }
```

- [ ] **Step 4: Hide the forecast in every non-rendering branch**

In `Tick`, add `_forecast?.Hide();` everywhere the other overlays hide, matching the existing `_bars?.Hide();` calls at lines 538, 543, 557, 580, 590. For each of those five sites, append `_forecast?.Hide();`. Also add it to `SafeTeardown` next to the other view teardowns.

- [ ] **Step 5: Build, install, and manually verify the bar**

Run the build+copy from Task 2 Step 4, restart the game, enter combat. Verify:
- The bar appears (default top-left under the game's top bar) and the stats line matches the Task 2 log numbers.
- Gaining block grows the shield segment; a partially-consumed shield shows a darker used portion.
- An unblocked attack shows a solid red segment eating into HP from the right; blocking it fully turns the bar green and the line reads `Safe ✓`.
- A lethal turn (max loss ≥ current HP) tints the panel red.
- Dragging the header moves the panel; it stays out of other branches (open a menu → it hides).

- [ ] **Step 6: Commit**

```bash
git add DamageForecastView.cs DamageChartsMod.cs
git commit -m "Render incoming-damage forecast as an HP-centric threat bar"
```

---

### Task 4: DoTs, self-damage cards, and range widening

Extend `ForecastReader.Build` to add the unblockable and conditional HP-loss sources that feed the min–max range: player damage-over-time powers (Bleed/Burn/Poison-like, bypass block) and self-damage cards in hand (conditional, "if played").

**Files:**
- Modify: `ForecastReader.cs`

**Interfaces:**
- Consumes: `Player.Creature`, `Creature.Powers` (or reflected equivalent), hand-card access (reflected); `TextHelper.SafeGetText` (existing).
- Produces: no signature change — `ForecastReader.Build` now appends `ForecastKind.Dot` (blockable:false, conditional:false) and `ForecastKind.SelfCard` (blockable:false, conditional:true) items.

- [ ] **Step 1: Add DoT + self-card reading to `ForecastReader`**

In `ForecastReader.Build`, inside the `me != null` block (after reading `block`), add:

```csharp
                ReadDots(me, items);
                ReadSelfDamageCards(players, localSlot, items);
```

Add these methods to `ForecastReader`:

```csharp
    private static readonly string[] DotKeywords = { "Bleed", "Burn", "Poison", "Regret", "Bomb", "Constrict" };

    // Player powers that tick HP loss this turn regardless of action. Damage magnitude is the power's
    // stack/amount for stacking DoTs (Bleed/Poison); best-effort for others.
    private static void ReadDots(Creature me, System.Collections.Generic.List<ForecastItem> items)
    {
        try
        {
            if (GetProp(me, "Powers") is IEnumerable powers)
            {
                foreach (var p in powers)
                {
                    if (p == null) continue;
                    string tn = p.GetType().Name;
                    string id = GetString(p, "Id", "PowerId") ?? tn;
                    if (!MatchesAny(tn, DotKeywords) && !MatchesAny(id, DotKeywords)) continue;
                    int amount = GetInt(p, "Amount", "Stacks", "Count", "Value");
                    if (amount <= 0) continue;
                    string label = SafeText(() => GetProp(p, "Title")) ?? Prettify(tn);
                    items.Add(new ForecastItem(label, ForecastKind.Dot, 1, amount, blockable: false, conditional: false));
                }
            }
        }
        catch { }
    }

    // Cards in hand whose description says they cost the player HP. Conditional — only counts if played,
    // so it widens the range MAX. Description-scan heuristic (no hard-coded card list).
    private static void ReadSelfDamageCards(IReadOnlyList<Player> players, int localSlot,
        System.Collections.Generic.List<ForecastItem> items)
    {
        try
        {
            if (localSlot < 0 || localSlot >= players.Count) return;
            object? hand = GetProp(players[localSlot], "Hand") ?? GetProp(players[localSlot], "CardsInHand");
            if (hand is not IEnumerable cards) return;
            foreach (var card in cards)
            {
                if (card == null) continue;
                string? desc = SafeText(() => GetProp(card, "Description") ?? GetProp(card, "Text") ?? GetProp(card, "Body"));
                if (desc == null) continue;
                int hpCost = ParseHpLoss(desc);
                if (hpCost <= 0) continue;
                string label = SafeText(() => GetProp(card, "Title")) ?? "Card";
                items.Add(new ForecastItem(label, ForecastKind.SelfCard, 1, hpCost, blockable: false, conditional: true));
            }
        }
        catch { }
    }

    // Matches "lose N HP" / "lose N Health" / "N HP loss" case-insensitively; returns the largest number found.
    private static int ParseHpLoss(string desc)
    {
        try
        {
            var rx = new System.Text.RegularExpressions.Regex(
                @"lose\s+(\d+)\s*(?:hp|health)|(\d+)\s*(?:hp|health)\s*loss",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            int max = 0;
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(desc))
            {
                string g = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (int.TryParse(g, out int n) && n > max) max = n;
            }
            return max;
        }
        catch { return 0; }
    }

    private static bool MatchesAny(string s, string[] subs)
    {
        foreach (var k in subs) if (Contains(s, k)) return true;
        return false;
    }

    private static string? GetString(object obj, params string[] names)
    {
        foreach (var n in names) { var v = GetProp(obj, n); if (v is string s && s.Length > 0) return s; }
        return null;
    }

    private static string? SafeText(Func<object?> getter)
    {
        try
        {
            var v = getter();
            if (v == null) return null;
            if (v is string s) return TextHelper.SafeGetText(() => s) ?? s;
            return TextHelper.SafeGetText(() => v.ToString() ?? "") ;
        }
        catch { return null; }
    }

    private static string Prettify(string typeName)
    {
        string n = typeName;
        foreach (var suffix in new[] { "PowerModel", "Power", "Model" })
            if (n.EndsWith(suffix)) { n = n[..^suffix.Length]; break; }
        return n.Length > 0 ? n : typeName;
    }
```

> If `TextHelper.SafeGetText`'s signature differs from `Func<T>`-returning-a-getter used here, mirror exactly how `DamageChartsMod.cs` already calls it (e.g. `TextHelper.SafeGetText(() => pend.Dealer!.Monster!.Title)`), adapting `SafeText` accordingly. Do NOT change `TextHelper`.

- [ ] **Step 2: Build, install, and manually verify the range sources**

Build+copy (Task 2 Step 4), restart, and test in a run where you can arrange:
- **A DoT on the player** (take Bleed/Poison/Burn): a red segment appears even when block covers all attacks; the `Loss` minimum is nonzero and equals the DoT; `Safe ✓` does NOT show. Confirm block does not reduce it.
- **A self-damage card in hand** (e.g. an Offering/Hemokinesis-style "Lose N HP" card): `Loss` shows a range `min–max` where `max−min` equals the card's HP cost; playing nothing keeps actual loss at `min`.
- If a known DoT/self-card is missed, widen `DotKeywords` or adjust the description property candidates in `ReadSelfDamageCards`, using the grep from Task 2 Step 5 (search for `Bleed`/`Poison` power type names and card `get_Description`/`get_Text`). Re-verify.

- [ ] **Step 3: Commit**

```bash
git add ForecastReader.cs
git commit -m "Add DoTs and self-damage cards to the damage forecast range"
```

---

### Task 5: Hover ledger

Hovering the threat bar shows an itemized tooltip: each enemy attack, each DoT (flagged unblockable), each self-damage card (flagged if-played), a hidden-intent note, and the Incoming/Block/Loss footer.

**Files:**
- Create: `ForecastLedgerView.cs`
- Modify: `DamageChartsMod.cs` (`EnsureViews`, `UpdateForecast`, hide branches, teardown)

**Interfaces:**
- Consumes: `ForecastResult`, `ForecastItem`, `ForecastKind` (Task 1); `DamageForecastView.IsHovered()`, `DamageForecastView.LastResult` (Task 3); `UiTheme` (existing).
- Produces: `class ForecastLedgerView` ctor `(Node root)`; `bool IsValid()`, `void Hide()`, `void Dispose()`, `void Render(ForecastResult r, Vector2 mouseGlobal)`.

- [ ] **Step 1: Write `ForecastLedgerView.cs`**

```csharp
using System;
using Godot;

namespace STS2_DamageCharts;

// Hover tooltip for the threat bar: one row per forecast item (attacks, DoTs flagged unblockable,
// self-damage cards flagged if-played), a hidden-intent note, and an Incoming/Block/Loss footer.
// Follows the cursor, flipping near screen edges. Native nodes; poll-driven from the tick.
internal sealed class ForecastLedgerView
{
    private const float BaseWidth = 268f;
    private const int MaxRows = 10;

    private readonly CanvasLayer _layer;
    private readonly Control _container;
    private readonly Panel _bg;
    private readonly System.Collections.Generic.List<Label> _labels = new();

    public ForecastLedgerView(Node root)
    {
        _layer = new CanvasLayer { Layer = 132, Name = "Sts2DamageChartsForecastLedger", Visible = false };
        root.AddChild(_layer);
        _container = new Control { Name = "ForecastLedger", MouseFilter = Control.MouseFilterEnum.Ignore };
        _layer.AddChild(_container);
        _bg = UiTheme.MakePanel(0.95f, 1f);
        _bg.SetPosition(Vector2.Zero);
        _container.AddChild(_bg);
    }

    public bool IsValid() => GodotObject.IsInstanceValid(_layer);
    public void Hide() { if (IsValid()) _layer.Visible = false; }
    public void Dispose() { if (IsValid()) _layer.QueueFree(); }

    public void Render(ForecastResult r, Vector2 mouseGlobal)
    {
        if (!IsValid()) return;
        try
        {
            _layer.Visible = true;
            Vector2 vp;
            try { vp = _container.GetViewportRect().Size; } catch { vp = new Vector2(1920, 1080); }
            float sc = UiTheme.Scale(vp);
            float W = BaseWidth * sc, rowH = 20f * sc, pad = 8f * sc;
            UiTheme.RestylePanel(_bg, 0.95f, sc);

            int cur = 0;
            float y = pad;

            var head = Next(ref cur, F(15, sc), UiTheme.Gold);
            head.Text = "Incoming Forecast";
            head.SetPosition(new Vector2(pad, y));
            y += 24f * sc;

            int shown = 0;
            var items = r.Items;
            for (int i = 0; i < items.Count && shown < MaxRows; i++)
            {
                var it = items[i];
                if (it.Kind == ForecastKind.Hidden || it.Total <= 0) continue;
                string text; Color color;
                switch (it.Kind)
                {
                    case ForecastKind.Dot:
                        text = $"{Truncate(it.Label, 18)}  {it.Total}  (unblock.)"; color = LossColor(); break;
                    case ForecastKind.SelfCard:
                        text = $"{Truncate(it.Label, 18)}  +{it.Total}  if played"; color = UiTheme.Gray; break;
                    default: // Attack / NonAttackHpLoss
                        string amt = it.Hits > 1 ? $"{it.Hits}×{it.DamagePerHit}  {it.Total}" : it.Total.ToString();
                        text = $"{Truncate(it.Label, 18)}  {amt}"; color = UiTheme.Cream; break;
                }
                var row = Next(ref cur, F(13, sc), color);
                row.Text = text;
                row.SetPosition(new Vector2(pad + 4f * sc, y));
                y += rowH; shown++;
            }

            if (r.HasHidden)
            {
                var hid = Next(ref cur, F(13, sc), UiTheme.Gray);
                hid.Text = "Hidden intent  ?";
                hid.SetPosition(new Vector2(pad + 4f * sc, y));
                y += rowH;
            }

            var sep = Next(ref cur, F(11, sc), new Color(UiTheme.Cream.R, UiTheme.Cream.G, UiTheme.Cream.B, 0.4f));
            sep.Text = "─────────";
            sep.SetPosition(new Vector2(pad, y));
            y += rowH;

            string lossStr = r.MinLoss == r.MaxLoss ? r.MinLoss.ToString() : $"{r.MinLoss}–{r.MaxLoss}";
            var foot = Next(ref cur, F(13, sc), r.IsLethal ? UiTheme.Red : UiTheme.Cream);
            foot.Text = $"In {r.Incoming}   Block {r.Block}   Loss {lossStr}";
            foot.SetPosition(new Vector2(pad, y));
            y += 22f * sc;

            float height = y + pad;
            _container.SetSize(new Vector2(W, height));
            _bg.SetSize(new Vector2(W, height));

            float off = 44f * sc;
            float x = mouseGlobal.X + off;
            if (x + W > vp.X) x = mouseGlobal.X - W - off;
            float py = mouseGlobal.Y + off;
            if (py + height > vp.Y) py = mouseGlobal.Y - height - off;
            x = Math.Clamp(x, 0f, Math.Max(0f, vp.X - W));
            py = Math.Clamp(py, 0f, Math.Max(0f, vp.Y - height));
            _container.SetPosition(new Vector2(x, py));

            for (int i = cur; i < _labels.Count; i++) _labels[i].Visible = false;
        }
        catch (Exception ex) { GD.PrintErr($"[STS2 Damage] forecast ledger error: {ex.Message}"); }
    }

    private static Color LossColor() => UiTheme.Red;
    private static int F(int b, float sc) => Math.Max(9, (int)Math.Round(b * sc));
    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private Label Next(ref int cursor, int fontSize, Color color)
    {
        Label lbl;
        if (cursor < _labels.Count) lbl = _labels[cursor];
        else { lbl = UiTheme.MakeLabel(fontSize, color); _container.AddChild(lbl); _labels.Add(lbl); }
        UiTheme.Apply(lbl, fontSize, color);
        lbl.Visible = true;
        cursor++;
        return lbl;
    }
}
```

- [ ] **Step 2: Create and drive the ledger in `DamageChartsMod.cs`**

Add the field with the other views (lines 56-58):

```csharp
    private static ForecastLedgerView? _forecastLedger;
```

In `EnsureViews`, after the `_forecast` creation (Task 3 Step 2), add:

```csharp
            if (_forecastLedger == null || !_forecastLedger.IsValid()) _forecastLedger = new ForecastLedgerView(root);
```

In `UpdateForecast`, after `_forecast.Render(r);` and the `UpdateMouse` line, add the hover drive:

```csharp
            if (!_forecast.IsDragging && _forecast.IsHovered() && _forecast.LastResult is { } last)
                _forecastLedger?.Render(last, RootMouse());
            else _forecastLedger?.Hide();
```

(`RootMouse()` is the existing helper used by the bars tooltip at line 569.)

- [ ] **Step 3: Hide/teardown the ledger with the other overlays**

Append `_forecastLedger?.Hide();` at each of the five hide sites where you added `_forecast?.Hide();` in Task 3 Step 4, and add it to `SafeTeardown`.

- [ ] **Step 4: Build, install, and manually verify the hover**

Build+copy (Task 2 Step 4), restart, enter combat, and hover the threat bar. Verify:
- The ledger lists each attacking enemy (with `N×D` for multi-hit), each DoT tagged `(unblock.)`, each self-damage card tagged `if played`, and a hidden-intent row when applicable.
- The footer matches the bar's Incoming/Block/Loss.
- The ledger follows the cursor and flips near screen edges; it hides while dragging and when a menu is open.

- [ ] **Step 5: Commit**

```bash
git add ForecastLedgerView.cs DamageChartsMod.cs
git commit -m "Add hover ledger breakdown for the incoming-damage forecast"
```

---

## Self-Review

**Spec coverage:**
- Three quantities (Incoming / Block / Est. HP loss) → `ForecastResult` fields + bar stats line (Tasks 1, 3). ✓
- HP-centric threat bar with shield buffer, green/solid-red/translucent-red segments → `DamageForecastView.LayoutBar` (Task 3). ✓
- Safe state when block covers attacks; lethal signal → `IsSafe`/`IsLethal` + render (Tasks 1, 3). ✓
- Min–max range; hidden intents as `+?` flag not a fake number → `Compute` + `HasHidden` handling (Tasks 1, 2). ✓
- HP-loss sources: enemy attacks (Task 2), DoTs bypassing block + self-damage cards conditional + enemy hidden flag (Tasks 2, 4). Enemy non-attack HP effects: modeled via `ForecastKind.NonAttackHpLoss` in the type system and rendered by the ledger's default branch; the reader currently classifies only known attacks/DoTs/self-cards and flags the rest as hidden — acceptable per spec ("included when we can read a concrete HP-loss amount; otherwise treated as uncertainty"). ✓
- Hover ledger breakdown → `ForecastLedgerView` (Task 5). ✓
- Standalone always-on draggable panel, position persisted as viewport fraction, hidden when blocking UI open → `DamageForecastView` + config + Tick hide branches (Tasks 2, 3). ✓
- Never break combat / reflection-by-name / self-disable → all reader/render in try/catch inside existing `Tick` guard (all tasks). ✓
- Config enable flag default on → `_showForecast` (Task 2). ✓

**Placeholder scan:** No "TBD"/"implement later". The two "if the signature differs, mirror the existing call" notes (Task 2 Step 5, Task 4 Step 1) are concrete verification/adjustment steps with exact greps, not deferred work — the code as written is complete and compiles against the candidate names.

**Type consistency:** `ForecastResult`, `ForecastItem` (ctor arg order `label, kind, hits, damagePerHit, blockable, conditional`), `ForecastKind`, `ForecastInputs`, `DamageForecast.Compute`, `DamageForecastView.{Render, UpdateMouse, IsHovered, LastResult, PositionFraction, IsDragging}`, `ForecastLedgerView.Render`, `ForecastReader.Build` — names and signatures used identically across tasks. Config fields `_showForecast`, `_fcPosXFrac`, `_fcPosYFrac`, `_forecast`, `_forecastLedger` consistent.
