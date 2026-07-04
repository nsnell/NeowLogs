# NeowLogs

Local coop run reports for Slay the Spire-style games.

This first version takes a JSON, JSONL, or CSV event log and shows a local HTML
leaderboard report in your browser. It is intentionally file based so you can
start testing with friends before any real game integration exists.

## Quick Start

Open `index.html` in your browser, then choose or drop a run log file.

Supported files:

- Slay the Spire 2 `.run` summary files
- NeowLogs mod `.jsonl` combat logs
- NeowLogs `.json`
- NeowLogs `.jsonl`
- NeowLogs `.csv`

You can also generate a standalone report from the command line:

```powershell
python .\neowlogs.py .\sample_run.json -o .\report.html
```

Then open `report.html`.

For NeowLogs mod `.jsonl` files on Windows, use the screenshot-friendly
PowerShell report generator:

```powershell
.\Generate-NeowLogsReport.ps1 "$env:LOCALAPPDATA\NeowLogs\runs\20260615-232258.jsonl" ".\reports\my-run-report.html"
```

Then open the generated HTML file in your browser. The report is fully local
and is designed to be easy to screenshot/share with friends.

## GitHub / Distribution

Recommended repo contents:

- `README.md` - project overview, install/use instructions, scoring notes.
- `index.html` - local drag-and-drop report viewer.
- `Generate-NeowLogsReport.ps1` and `neowlogs.py` - optional report generators.
- `sample_run.json` - sample data for quick testing.
- `mods/neowlogs-sts2/` - StS2 mod source, manifest, schema, and docs.
- `.gitignore` - keeps local logs, generated reports, and build output private.

Do not commit personal run logs from `%LOCALAPPDATA%\NeowLogs\runs`, generated
HTML reports, `.godot`, `bin`, `obj`, or Steam install files.

For installable releases, attach a zip containing only the built mod folder:

```text
NeowLogs/
  NeowLogs.dll
  NeowLogs.json
  NeowLogs.pdb optional
```

Users should unzip that folder into:

```text
<Slay the Spire 2 install>\mods\NeowLogs\
```

## What It Tracks

- Damage by player, act, and source.
- Block gained.
- Healing.
- Utility actions.
- Vulnerable damage attribution, so a utility player can get credit for damage
  enabled by their vulnerable application.
- Per-act leaders and run MVP-style summaries.

For real `.run` summary files, the app currently tracks the stats that are
actually present in those summaries:

- Run metadata: seed, ascension, build/version, victory, final act/floor.
- Players, characters, deck size, relic count, badges.
- Floor-by-floor damage taken, healing, HP snapshots, gold changes, card gains,
  card removals, upgrades, potion use, room type, enemy/encounter id, and turns
  taken.

`.run` files do not appear to include full combat-event details like exact
damage dealt, block generated, per-card hits, or vulnerable/weak attribution.
Those will need a later combat logger/mod event export.

## Utility Score

Utility score is only computed when combat events are present. It is not
computed for `.run` summary files because those files do not include enough
combat detail.

Current formula:

```text
utility score =
  vulnerable damage enabled
```

Damage is always the final damage dealt by the hitter. Utility is only the
extra delta caused by setup. If Vulnerable turns a 20-damage hit into 30, the
hitter gets 30 damage and the Vulnerable applier gets 10 utility.

Block, healing, card draw, energy, and status applications are tracked as
details, but they are not folded into the Utility score. For `.run` files,
utility shows as `N/A` until we have a combat-event logger.

The mod also tracks hidden detail stats for damage blocked and prevented damage.
Weak/strength-reduction utility is credited only when the combat event exposes
enough information to compare original incoming damage against final reduced
damage. Pet/summon damage is attributed to the owner when owner data is exposed.

## StS2 Mod

The mod scaffold lives in:

```text
mods/neowlogs-sts2
```

It includes:

- A JSONL combat log writer.
- A live in-game meter panel.
- A stats accumulator for raw damage, utility damage, block, healing, damage
  taken, and contribution score.
- A Godot/GDScript adapter layer for wiring into StS2/mod-loader combat hooks.
- A JSON schema and sample combat log.

The intentionally unstable part is:

```text
mods/neowlogs-sts2/scripts/sts2/Sts2HookAdapter.gd
```

Once the active StS2 mod loader exposes run/combat/card/damage/status hooks,
that adapter should translate those callbacks into NeowLogs events. The rest of
the mod can stay stable.

## Input Format

The preferred format is JSON:

```json
{
  "run_name": "Friday Coop Run",
  "players": ["Ari", "Bo", "Cy"],
  "events": [
    {
      "act": 1,
      "turn": 2,
      "player": "Ari",
      "type": "apply_status",
      "status": "vulnerable",
      "target": "Jaw Worm",
      "amount": 2
    },
    {
      "act": 1,
      "turn": 2,
      "player": "Bo",
      "type": "damage",
      "target": "Jaw Worm",
      "amount": 18,
      "base_amount": 12
    }
  ]
}
```

JSONL is also supported, with one event object per line. CSV is supported when
it has headers such as:

```csv
act,turn,player,type,target,amount,base_amount,status
1,2,Ari,apply_status,Jaw Worm,2,,vulnerable
1,2,Bo,damage,Jaw Worm,18,12,
```

### Vulnerable Attribution

For damage events, vulnerable contribution is credited in this order:

1. `vulnerable_applied_by` on the damage event.
2. The most recent `apply_status` event with `status: "vulnerable"` for that
   target.

The credited value is:

- `vulnerable_bonus` when provided.
- Otherwise `amount - base_amount` when both fields are present.
- Otherwise `round(amount / 3)` as a rough fallback.

This means a player running a utility deck can appear on the board for damage
they enabled, not only damage they personally dealt.

## Event Types

The parser recognizes these event types:

- `damage`
- `block`
- `heal`
- `draw`
- `energy`
- `apply_status`
- `utility`
- `card_play`
- `relic_trigger`

Unknown event types are still counted as utility touches, so early messy logs
remain useful.
