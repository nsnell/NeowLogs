# NeowLogs StS2 Mod

NeowLogs is a WarcraftLogs-style combat logger and live meter concept for
Slay the Spire 2 co-op.

This folder is the mod-side project:

- Capture combat events while a run is being played.
- Show an in-game leaderboard/meter panel.
- Write JSONL event logs that the local NeowLogs report viewer can load later.

## Current Status

This now follows the current StS2 mod template shape:

- C# / Godot .NET project
- `Godot.NET.Sdk/4.5.1`
- `net9.0`
- `[ModInitializer]`
- Harmony patching
- `NeowLogs.json` mod manifest

The current public docs do not expose a single high-level combat-log API.
Instead, the mod uses Harmony patches against likely command/controller targets
and logs if targets are missing. If StS2 changes internal names, update:

```text
NeowLogsCode/Patches/RuntimePatchRegistry.cs
NeowLogsCode/Patches/LifecyclePatches.cs
```

Missing patch targets should not prevent the mod from loading. If NeowLogs
appears in Mod Settings but does not capture combat details, check the game log
for `NeowLogs did not find ... targets`.

## Setup

Install:

- A C# IDE such as Rider or Visual Studio.
- The .NET SDK.
- Megadot or Godot matching the current StS2 modding guide.

BaseLib is not required for NeowLogs. If BaseLib is installed separately and
fails to load after a StS2 update, NeowLogs can still run without it.

If automatic path discovery fails, edit:

```text
Directory.Build.props
```

Set:

```xml
<Sts2InstallDir>C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2</Sts2InstallDir>
```

## Build And Load

From this folder:

```powershell
dotnet build
```

If `Godot.NET.Sdk/4.5.1` cannot be resolved, install/open the project using the
same Megadot/Godot setup from the StS2 mod template guide, or create the project
from the official template first:

```powershell
dotnet new install Alchyr.Sts2.Templates
```

Then compare/copy this `NeowLogsCode` folder and manifest into that generated
project.

The project copies these files to:

```text
<Slay the Spire 2 install>\mods\NeowLogs\
```

Files:

```text
NeowLogs.dll
NeowLogs.pdb
NeowLogs.json
```

Launch StS2. On first modded launch, the game may ask you to confirm mods and
restart. After that, check:

```text
Settings -> Mod Settings
```

NeowLogs should appear there.

## Multiplayer Note

The manifest sets:

```json
"affects_gameplay": false
```

The current modding guide says non-gameplay mods are not checked when joining a
multiplayer lobby. If this mod ever starts changing game state, flip that to
`true`.

## Output Files

Logs are written as JSONL:

```text
%LOCALAPPDATA%\NeowLogs\runs\<run_id>.jsonl
```

Each line is one event. The report viewer can ingest JSONL directly.

The live meter also keeps a computed resume checkpoint:

```text
%LOCALAPPDATA%\NeowLogs\state\active-run.json
```

That file stores the active `run_id`, best-known `run_key`, current act/floor,
player totals, manual player names, and active utility ownership state. On game
load, NeowLogs restores this checkpoint first and appends new events to the same
JSONL log. When a run ends, the checkpoint is moved to:

```text
%LOCALAPPDATA%\NeowLogs\state\completed-runs\<run_id>.state.json
```

## Event Shape

```json
{
  "schema_version": 1,
  "run_id": "20260615-211500",
  "combat_id": "act1_floor3_jaw_worm",
  "timestamp_ms": 123456,
  "act": 1,
  "floor": 3,
  "turn": 2,
  "actor_player_id": "p1",
  "actor_name": "Mira",
  "target_id": "enemy_1",
  "target_name": "Jaw Worm",
  "event_type": "damage_dealt",
  "amount": 30,
  "base_amount": 20,
  "source_type": "card",
  "source_name": "Heavy Strike",
  "metadata": {
    "amplified_by": [
      {
        "type": "vulnerable",
        "applied_by_player_id": "p2",
        "applied_by_name": "Sol",
        "bonus_damage": 10
      }
    ]
  }
}
```

## Phase 1 Metrics

The live meter computes:

- Raw damage
- Direct damage
- Utility damage enabled through vulnerable/amplification metadata
- Block gained
- Damage taken
- Damage blocked, tracked as a hidden detail stat
- Healing
- Lowest HP
- Cards played
- Damage per card
- Damage per energy
- Utility score

## Live Meter UI

The in-game meter is a compact bottom-right overlay inspired by Details-style
raid meters. It uses stock Godot controls only.

Tabs:

- `Dmg` - damage dealt
- `Block` - block generated
- `Util` - utility score
- `Taken` - damage taken
- `Heal` - healing done

Each tab shows the current leader, ranked player rows, and proportional bars.
Use the small `-` / `+` button on the meter header to collapse or expand it.

## Attribution Rules

Damage is split into:

- **Direct damage:** credited to the player who dealt the hit.
- **Utility damage:** credited to the player whose debuff/buff/resource effect
  enabled extra damage.

For vulnerable-style amplification:

```text
direct damage = damage amount - bonus damage
utility damage = bonus damage, credited to applied_by_player_id
```

The report still preserves the raw event, so attribution rules can be changed
later without losing data.

For vulnerable, utility damage is the delta only. If a hit lands for 30 because
vulnerable raised a 20-damage hit by 10, the hitter keeps their 20 direct damage
credit and the vulnerable applier gets 10 utility damage.

Weak, strength reduction, and similar prevention effects use prevention
attribution when the damage result exposes enough information: compare original
incoming damage to final damage after reduction and credit the applier with that
prevented-damage delta. Blocked damage is tracked separately and is not shown on
the main meter yet.

Pet/summon damage, such as Otsy-style damage, is attributed to the pet owner's
player when the creature exposes a `PetOwner`, `_petOwner`, or `Owner` reference.

For lethal hits, the mod prefers overkill-friendly damage result fields when
available so the last attack of combat can still receive the full attack value
rather than only the enemy's remaining HP.

## Integration Point

Combat targets are registered in:

```text
NeowLogsCode/Patches/RuntimePatchRegistry.cs
```

Lifecycle targets are registered in:

```text
NeowLogsCode/Patches/LifecyclePatches.cs
```

The mod logs missing patch targets to the StS2 log. If no combat events are
captured, decompile `sts2.dll`, find the current command/controller names for
attack, block, healing, powers, card play, run start, and combat start/end, then
update those two files.
