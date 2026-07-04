# StS2 Hook Map

This document lists the events NeowLogs needs from Slay the Spire 2 or the
active mod loader.

The mod code is intentionally split so these are the unstable areas:

```text
NeowLogsCode/Patches/RuntimePatchRegistry.cs
NeowLogsCode/Patches/LifecyclePatches.cs
```

## Required For Phase 1

| Game event | Patch target area | Notes |
| --- | --- | --- |
| Run starts | run controller start/begin method | Needs run id, seed, ascension, party/player info when available. |
| Run ends | run controller end method | Needs victory/loss, final act/floor. |
| Combat starts | combat controller start/begin method | Needs act, floor, encounter id/name. |
| Combat ends | combat controller end method | Needs turns taken and result if available. |
| Turn starts | turn controller start method | Needed for per-turn leaderboards. |
| Card played | card play action/command | Needed for damage/card and damage/energy. |
| Damage dealt | attack/damage command | Needs amount, base amount, source, target. |
| Damage taken | damage receiver/player HP loss command | Needs amount and current HP for risk stats. |
| Block gained | block command | Needed for defense leaderboards. |
| Healing done | heal command | Needed for medic/support awards. |
| Buff/debuff applied | power apply command | Needed for vulnerable/weak/poison attribution. |

## Amplification Attribution

Best case, the damage hook provides `amplified_by` directly:

```json
[
  {
    "type": "vulnerable",
    "applied_by_player_id": "p2",
    "applied_by_name": "Sol",
    "bonus_damage": 10
  }
]
```

If StS2 does not expose this directly, the adapter can maintain a small status
tracker:

1. On `debuff_applied`, remember status, target, source player, stacks/duration.
2. On `damage_dealt`, compare `amount` and `base_amount`.
3. Credit the bonus to the most recent matching source player.

## Nice-To-Have Later

- Poison tick owner
- Summon/minion owner
- Damage prevented
- Intangible prevented
- Overkill damage
- Discard/exhaust events
- Card draw ownership
- Energy given to another player
- Enemy intent target selection
