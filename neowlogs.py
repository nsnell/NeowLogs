#!/usr/bin/env python3
"""Generate a local coop leaderboard report from a run event file."""

from __future__ import annotations

import argparse
import csv
import html
import json
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Any


SUPPORT_STATUSES = {
    "vulnerable",
    "weak",
    "frail",
    "poison",
    "mark",
    "lock_on",
    "strength_down",
    "artifact_strip",
}


@dataclass
class PlayerStats:
    damage: int = 0
    direct_damage: int = 0
    vulnerable_damage_enabled: int = 0
    block: int = 0
    damage_taken: int = 0
    healing: int = 0
    lowest_hp: int | None = None
    cards_drawn: int = 0
    energy_given: int = 0
    utility_events: int = 0
    cards_added: int = 0
    cards_removed: int = 0
    cards_upgraded: int = 0
    gold_gained: int = 0
    gold_spent: int = 0
    turns: int = 0
    statuses_applied: Counter[str] = field(default_factory=Counter)
    sources: Counter[str] = field(default_factory=Counter)

    @property
    def support_score(self) -> int:
        return (
            self.vulnerable_damage_enabled
            + self.block
            + self.healing * 2
            + self.cards_drawn * 4
            + self.energy_given * 8
            + self.utility_events * 5
            + sum(self.statuses_applied.values()) * 6
        )


def to_int(value: Any, default: int = 0) -> int:
    if value is None or value == "":
        return default
    try:
        return int(float(value))
    except (TypeError, ValueError):
        return default


def normalize_event(raw: dict[str, Any]) -> dict[str, Any]:
    event = {str(k).strip(): v for k, v in raw.items()}
    event["type"] = str(event.get("type") or event.get("event") or event.get("event_type") or "").strip().lower()
    event["player"] = str(event.get("player") or event.get("actor") or event.get("actor_name") or event.get("actor_player_id") or "Unknown").strip()
    event["target"] = str(event.get("target") or event.get("target_name") or event.get("target_id") or "").strip()
    event["source"] = str(event.get("source") or event.get("card") or event.get("ability") or event.get("source_name") or event.get("source_type") or "").strip()
    metadata = event.get("metadata") if isinstance(event.get("metadata"), dict) else {}
    event["status"] = str(event.get("status") or event.get("debuff") or metadata.get("status") or "").strip().lower()
    event["act"] = to_int(event.get("act"), 0)
    event["floor"] = to_int(event.get("floor"), 0)
    event["turn"] = to_int(event.get("turn"), 0)
    event["amount"] = to_int(event.get("amount"), 0)
    event["base_amount"] = to_int(event.get("base_amount"), event["amount"])
    return event


def clean_game_id(value: Any) -> str:
    text = str(value or "Unknown")
    if "." in text:
        text = text.split(".", 1)[1]
    return text.replace("_", " ").title()


def convert_sts_run_summary(data: dict[str, Any], run_name: str) -> dict[str, Any]:
    players = []
    for index, player in enumerate(data.get("players", []), start=1):
        players.append(
            {
                "id": str(player.get("id") or index),
                "display_name": f"{clean_game_id(player.get('character'))} {index}",
                "character": clean_game_id(player.get("character")),
                "seat_index": index - 1,
                "deck_size": len(player.get("deck", [])),
                "relic_count": len(player.get("relics", [])),
                "badges": [badge.get("id") for badge in player.get("badges", [])],
            }
        )
    by_id = {player["id"]: player for player in players}
    events: list[dict[str, Any]] = []
    floor_number = 0

    for act_index, act_floors in enumerate(data.get("map_point_history", []), start=1):
        for floor in act_floors:
            floor_number += 1
            rooms = floor.get("rooms", [])
            room = rooms[-1] if rooms else {}
            turns = to_int(room.get("turns_taken"))
            events.append(
                normalize_event(
                    {
                        "act": act_index,
                        "floor": floor_number,
                        "player": "Team",
                        "type": "floor_entered",
                        "source": clean_game_id(floor.get("map_point_type") or room.get("room_type")),
                        "amount": turns,
                    }
                )
            )
            if turns > 0:
                events.append(
                    normalize_event(
                        {
                            "act": act_index,
                            "floor": floor_number,
                            "player": "Team",
                            "type": "combat_ended",
                            "source": clean_game_id(room.get("model_id") or floor.get("map_point_type")),
                            "amount": turns,
                        }
                    )
                )

            for stat in floor.get("player_stats", []):
                player = by_id.get(str(stat.get("player_id")), {"display_name": str(stat.get("player_id") or "Unknown")})
                base = {"act": act_index, "floor": floor_number, "player": player["display_name"]}
                for event_type, key in [
                    ("damage_taken", "damage_taken"),
                    ("heal", "hp_healed"),
                    ("gold_gained", "gold_gained"),
                    ("gold_spent", "gold_spent"),
                    ("max_hp_gained", "max_hp_gained"),
                ]:
                    amount = to_int(stat.get(key))
                    if amount > 0:
                        events.append(normalize_event({**base, "type": event_type, "amount": amount, "source": clean_game_id(key)}))
                if stat.get("current_hp") is not None:
                    events.append(normalize_event({**base, "type": "hp_snapshot", "amount": to_int(stat.get("current_hp")), "source": "Current HP"}))
                for key, event_type in [
                    ("cards_gained", "card_gained"),
                    ("cards_removed", "card_removed"),
                    ("upgraded_cards", "card_upgraded"),
                    ("potion_used", "potion_used"),
                ]:
                    for item in stat.get(key, []) or []:
                        source = item.get("id") if isinstance(item, dict) else item
                        events.append(normalize_event({**base, "type": event_type, "amount": 1, "source": clean_game_id(source)}))

    for player in players:
        events.append(normalize_event({"act": 0, "floor": 0, "player": player["display_name"], "type": "deck_summary", "amount": player["deck_size"], "source": f"{player['deck_size']} cards, {player['relic_count']} relics"}))
        for badge in player["badges"]:
            events.append(normalize_event({"act": 0, "floor": 0, "player": player["display_name"], "type": "award", "amount": 1, "source": clean_game_id(badge)}))

    return {
        "run_name": f"{run_name} - StS Run Summary",
        "run": {
            "seed": data.get("seed"),
            "ascension": data.get("ascension"),
            "game_version": data.get("build_id"),
            "victory": data.get("win"),
            "final_act": len(data.get("acts", [])),
            "final_floor": floor_number,
            "run_time": data.get("run_time"),
            "source_format": "sts_run_summary",
        },
        "players": [player["display_name"] for player in players],
        "events": events,
    }


def load_events(path: Path) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    suffix = path.suffix.lower()
    if suffix in {".json", ".run"}:
        data = json.loads(path.read_text(encoding="utf-8"))
        if isinstance(data, dict) and data.get("map_point_history") and data.get("players"):
            converted = convert_sts_run_summary(data, path.stem)
            return converted, converted["events"]
        if isinstance(data, list):
            return {"run_name": path.stem}, [normalize_event(item) for item in data]
        return data, [normalize_event(item) for item in data.get("events", [])]

    if suffix == ".jsonl":
        events = []
        for line in path.read_text(encoding="utf-8").splitlines():
            if line.strip():
                events.append(normalize_event(json.loads(line)))
        return {"run_name": path.stem}, events

    if suffix == ".csv":
        with path.open(newline="", encoding="utf-8") as file:
            return {"run_name": path.stem}, [normalize_event(row) for row in csv.DictReader(file)]

    raise ValueError(f"Unsupported input type: {path.suffix}. Use .json, .jsonl, or .csv.")


def empty_stats() -> PlayerStats:
    return PlayerStats()


def analyze(meta: dict[str, Any], events: list[dict[str, Any]]) -> dict[str, Any]:
    players = set(meta.get("players") or [])
    players.update(event["player"] for event in events if event.get("player"))
    players.discard("")

    totals: dict[str, PlayerStats] = {player: empty_stats() for player in sorted(players)}
    by_act: dict[int, dict[str, PlayerStats]] = defaultdict(lambda: defaultdict(empty_stats))
    act_totals: dict[int, Counter[str]] = defaultdict(Counter)
    vulnerable_by_target: dict[str, str] = {}
    timeline: list[dict[str, Any]] = []

    for event in sorted(events, key=lambda item: (item.get("act", 0), item.get("turn", 0))):
        player = event["player"]
        totals.setdefault(player, empty_stats())
        act_stats = by_act[event["act"]][player]
        kind = event["type"]
        amount = event["amount"]
        source = event["source"] or kind or "Unknown"

        if kind in {"damage", "damage_dealt"}:
            vulnerable_player = str(event.get("vulnerable_applied_by") or "").strip()
            if not vulnerable_player and event["target"]:
                vulnerable_player = vulnerable_by_target.get(event["target"], "")

            vulnerable_bonus = to_int(event.get("vulnerable_bonus"), amount - event["base_amount"])
            metadata = event.get("metadata") if isinstance(event.get("metadata"), dict) else {}
            amplified_by = metadata.get("amplified_by", []) if isinstance(metadata.get("amplified_by", []), list) else []
            if amplified_by:
                vulnerable_bonus = sum(to_int(item.get("bonus_damage")) for item in amplified_by if isinstance(item, dict))
            if vulnerable_bonus <= 0 and vulnerable_player:
                vulnerable_bonus = round(amount / 3)

            for bucket in (totals[player], act_stats):
                bucket.damage += amount
                bucket.direct_damage += max(amount - max(vulnerable_bonus, 0), 0)
                bucket.sources[source] += amount

            act_totals[event["act"]]["damage"] += amount

            if amplified_by:
                for item in amplified_by:
                    if not isinstance(item, dict):
                        continue
                    support_player = str(item.get("applied_by_name") or item.get("applied_by_player_id") or "").strip()
                    bonus = to_int(item.get("bonus_damage"))
                    if support_player and bonus > 0:
                        totals.setdefault(support_player, empty_stats())
                        totals[support_player].vulnerable_damage_enabled += bonus
                        by_act[event["act"]][support_player].vulnerable_damage_enabled += bonus
            elif vulnerable_player and vulnerable_bonus > 0:
                totals.setdefault(vulnerable_player, empty_stats())
                totals[vulnerable_player].vulnerable_damage_enabled += vulnerable_bonus
                by_act[event["act"]][vulnerable_player].vulnerable_damage_enabled += vulnerable_bonus

        elif kind in {"block", "block_gained"}:
            for bucket in (totals[player], act_stats):
                bucket.block += amount
            act_totals[event["act"]]["block"] += amount

        elif kind in {"damage_taken", "hp_lost"}:
            for bucket in (totals[player], act_stats):
                bucket.damage_taken += amount

        elif kind in {"heal", "healing_done"}:
            for bucket in (totals[player], act_stats):
                bucket.healing += amount
            act_totals[event["act"]]["healing"] += amount

        elif kind == "hp_snapshot":
            for bucket in (totals[player], act_stats):
                bucket.lowest_hp = amount if bucket.lowest_hp is None else min(bucket.lowest_hp, amount)

        elif kind in {"draw", "card_drawn"}:
            for bucket in (totals[player], act_stats):
                bucket.cards_drawn += amount

        elif kind in {"energy", "energy_gained"}:
            for bucket in (totals[player], act_stats):
                bucket.energy_given += amount

        elif kind == "gold_gained":
            for bucket in (totals[player], act_stats):
                bucket.gold_gained += amount

        elif kind == "gold_spent":
            for bucket in (totals[player], act_stats):
                bucket.gold_spent += amount

        elif kind == "card_gained":
            for bucket in (totals[player], act_stats):
                bucket.cards_added += amount

        elif kind == "card_removed":
            for bucket in (totals[player], act_stats):
                bucket.cards_removed += amount

        elif kind == "card_upgraded":
            for bucket in (totals[player], act_stats):
                bucket.cards_upgraded += amount

        elif kind == "combat_ended":
            for bucket in (totals[player], act_stats):
                bucket.turns += amount

        elif kind in {"apply_status", "debuff_applied", "buff_applied", "power_applied"}:
            status = event["status"] or "status"
            for bucket in (totals[player], act_stats):
                bucket.statuses_applied[status] += max(amount, 1)
                if status in SUPPORT_STATUSES:
                    bucket.utility_events += 1
            if status == "vulnerable" and event["target"]:
                vulnerable_by_target[event["target"]] = player

        elif kind in {"utility", "relic_triggered"}:
            for bucket in (totals[player], act_stats):
                bucket.utility_events += 1

        timeline.append(event)

    return {
        "meta": meta,
        "players": sorted(players),
        "totals": totals,
        "by_act": by_act,
        "act_totals": act_totals,
        "timeline": timeline,
    }


def rank(stats: dict[str, PlayerStats], key: str) -> list[tuple[str, int]]:
    rows = []
    for player, player_stats in stats.items():
        value = getattr(player_stats, key)
        if value is None:
            value = 999999 if key == "lowest_hp" else 0
        rows.append((player, int(value() if callable(value) else value)))
    return sorted(rows, key=lambda item: item[1], reverse=(key != "lowest_hp"))


def percentage(value: int, total: int) -> str:
    if total <= 0:
        return "0%"
    return f"{round(value / total * 100)}%"


def top_source(stats: PlayerStats) -> str:
    if not stats.sources:
        return "None"
    source, amount = stats.sources.most_common(1)[0]
    return f"{html.escape(source)} ({amount})"


def render_bar(value: int, max_value: int) -> str:
    width = 0 if max_value <= 0 else max(4, round(value / max_value * 100))
    return f'<span class="bar"><span style="width: {width}%"></span></span>'


def render_leaderboard(title: str, rows: list[tuple[str, int]]) -> str:
    max_value = max((value for _, value in rows), default=0)
    items = []
    for index, (player, value) in enumerate(rows, start=1):
        items.append(
            "<tr>"
            f"<td class=\"rank\">{index}</td>"
            f"<td>{html.escape(player)}</td>"
            f"<td>{render_bar(value, max_value)}</td>"
            f"<td class=\"number\">{value}</td>"
            "</tr>"
        )
    return (
        f"<section class=\"panel\"><h2>{html.escape(title)}</h2>"
        "<table><tbody>"
        + "".join(items)
        + "</tbody></table></section>"
    )


def render_report(analysis: dict[str, Any]) -> str:
    meta = analysis["meta"]
    totals: dict[str, PlayerStats] = analysis["totals"]
    by_act = analysis["by_act"]
    timeline = analysis["timeline"]
    run_name = str(meta.get("run_name") or "Coop Run")
    total_damage = sum(stats.damage for stats in totals.values())
    total_block = sum(stats.block for stats in totals.values())
    total_damage_taken = sum(stats.damage_taken for stats in totals.values())
    run = meta.get("run", {})
    is_summary_run = run.get("source_format") == "sts_run_summary"
    total_support = 0 if is_summary_run else sum(stats.support_score for stats in totals.values())

    mvp_damage = rank(totals, "damage")[0] if totals else ("None", 0)
    mvp_block = rank(totals, "block")[0] if totals else ("None", 0)
    mvp_support = ("Unavailable", 0) if is_summary_run else (rank(totals, "support_score")[0] if totals else ("None", 0))
    lowest_hp_rows = [row for row in rank(totals, "lowest_hp") if row[1] != 999999]
    mvp_risk = lowest_hp_rows[0] if lowest_hp_rows else ("None", 0)

    player_rows = []
    for player in sorted(totals):
        stats = totals[player]
        damage_cell = "N/A" if is_summary_run else str(stats.damage)
        direct_cell = "N/A" if is_summary_run else str(stats.direct_damage)
        vuln_cell = "N/A" if is_summary_run else str(stats.vulnerable_damage_enabled)
        block_cell = "N/A" if is_summary_run else str(stats.block)
        support_cell = "N/A" if is_summary_run else str(stats.support_score)
        player_rows.append(
            "<tr>"
            f"<td>{html.escape(player)}</td>"
            f"<td class=\"number\">{damage_cell}</td>"
            f"<td class=\"number\">{direct_cell}</td>"
            f"<td class=\"number\">{vuln_cell}</td>"
            f"<td class=\"number\">{block_cell}</td>"
            f"<td class=\"number\">{stats.damage_taken}</td>"
            f"<td class=\"number\">{stats.healing}</td>"
            f"<td class=\"number\">{stats.lowest_hp if stats.lowest_hp is not None else '-'}</td>"
            f"<td class=\"number\">{support_cell}</td>"
            f"<td>{top_source(stats)}</td>"
            "</tr>"
        )

    act_sections = []
    for act in sorted(by_act):
        stats = dict(by_act[act])
        third_board = render_leaderboard("Healing", rank(stats, "healing")) if is_summary_run else render_leaderboard("Support", rank(stats, "support_score"))
        act_sections.append(
            "<section class=\"act\">"
            f"<h2>Act {act}</h2>"
            "<div class=\"grid three\">"
            + render_leaderboard("Damage Done: N/A" if is_summary_run else "Damage", [] if is_summary_run else rank(stats, "damage"))
            + render_leaderboard("Block: N/A" if is_summary_run else "Block", [] if is_summary_run else rank(stats, "block"))
            + third_board
            + "</div>"
            "</section>"
        )

    timeline_rows = []
    for event in timeline[-30:]:
        detail = event.get("source") or event.get("status") or event.get("note") or ""
        timeline_rows.append(
            "<tr>"
            f"<td>Act {event.get('act', 0)}</td>"
            f"<td>{event.get('turn', 0)}</td>"
            f"<td>{html.escape(event.get('player', 'Unknown'))}</td>"
            f"<td>{html.escape(event.get('type', 'event'))}</td>"
            f"<td>{html.escape(str(detail))}</td>"
            f"<td class=\"number\">{event.get('amount', 0)}</td>"
            "</tr>"
        )

    generated = datetime.now().strftime("%Y-%m-%d %H:%M")
    run_meta = " | ".join(
        str(item)
        for item in [
            "Victory" if run.get("victory") is True else "Loss" if run.get("victory") is False else "",
            f"Ascension {run.get('ascension')}" if run.get("ascension") is not None else "",
            f"Seed {run.get('seed')}" if run.get("seed") else "",
            run.get("game_version") or "",
        ]
        if item
    )
    support_help = "Support score is only computed when combat events are present. Formula: vulnerable damage enabled + block + healing x2 + cards drawn x4 + energy given x8 + explicit utility events x5 + status stacks x6. StS .run summaries do not include enough combat detail for this attribution."
    support_notice = (
        "<section class=\"panel notice\"><strong>Combat attribution unavailable for this file.</strong>"
        "<p class=\"subtle\">StS .run summaries do not include outgoing damage, generated block, card-by-card combat actions, or vulnerable/weak attribution. "
        "This report uses the reliable summary fields instead: damage taken, healing, HP, gold, cards, potions, rooms, and turns.</p></section>"
        if is_summary_run
        else ""
    )
    return f"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{html.escape(run_name)} - NeowLogs</title>
  <style>
    :root {{
      color-scheme: dark;
      --bg: #111318;
      --panel: #1a1d25;
      --panel-2: #202531;
      --text: #eef1f6;
      --muted: #a9b0c0;
      --line: #303747;
      --gold: #f0b84d;
      --red: #e66767;
      --blue: #75a7ff;
      --green: #65d18b;
    }}
    * {{ box-sizing: border-box; }}
    body {{
      margin: 0;
      background: var(--bg);
      color: var(--text);
      font: 14px/1.5 system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }}
    header {{
      padding: 28px 32px 22px;
      border-bottom: 1px solid var(--line);
      background: #171a22;
    }}
    h1, h2, h3, p {{ margin-top: 0; }}
    h1 {{ margin-bottom: 6px; font-size: clamp(28px, 4vw, 44px); }}
    h2 {{ font-size: 18px; margin-bottom: 14px; }}
    h3 {{ color: var(--muted); font-size: 12px; letter-spacing: .08em; text-transform: uppercase; }}
    main {{ padding: 24px 32px 40px; }}
    .subtle {{ color: var(--muted); }}
    .grid {{ display: grid; gap: 16px; }}
    .three {{ grid-template-columns: repeat(3, minmax(0, 1fr)); }}
    .four {{ grid-template-columns: repeat(4, minmax(0, 1fr)); }}
    .panel, .stat {{
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 16px;
    }}
    .stat strong {{ display: block; font-size: 28px; line-height: 1.1; }}
    .stat span {{ color: var(--muted); }}
    .hint {{
      display: inline-grid;
      place-items: center;
      width: 18px;
      height: 18px;
      margin-left: 6px;
      border: 1px solid var(--line);
      border-radius: 50%;
      color: var(--muted);
      font-size: 12px;
      cursor: help;
    }}
    .notice {{ border-left: 3px solid var(--gold); background: #1d1b15; }}
    table {{ width: 100%; border-collapse: collapse; }}
    th, td {{ border-bottom: 1px solid var(--line); padding: 9px 8px; text-align: left; vertical-align: middle; }}
    th {{ color: var(--muted); font-size: 12px; font-weight: 600; text-transform: uppercase; }}
    tr:last-child td {{ border-bottom: 0; }}
    .number, .rank {{ text-align: right; font-variant-numeric: tabular-nums; }}
    .rank {{ color: var(--gold); width: 36px; }}
    .bar {{
      display: block;
      height: 8px;
      overflow: hidden;
      border-radius: 999px;
      background: #0f1117;
      min-width: 90px;
    }}
    .bar span {{ display: block; height: 100%; background: linear-gradient(90deg, var(--blue), var(--green)); }}
    .summary {{ margin-bottom: 16px; }}
    .section-title {{ margin: 28px 0 12px; }}
    .act {{ margin-top: 18px; }}
    .table-panel {{ overflow-x: auto; }}
    @media (max-width: 900px) {{
      header, main {{ padding-left: 18px; padding-right: 18px; }}
      .three, .four {{ grid-template-columns: 1fr; }}
    }}
  </style>
</head>
<body>
  <header>
    <h1>{html.escape(run_name)}</h1>
    <p class="subtle">{html.escape(run_meta + ' | ' if run_meta else '')}NeowLogs coop report generated {generated}</p>
  </header>
  <main>
    {support_notice}
    <section class="grid four summary">
      <div class="stat"><h3>{'Damage Done' if is_summary_run else 'Total Damage'}</h3><strong>{'N/A' if is_summary_run else total_damage}</strong><span>{'.run summaries do not include outgoing damage.' if is_summary_run else html.escape(mvp_damage[0]) + ' led with ' + str(mvp_damage[1]) + '.'}</span></div>
      <div class="stat"><h3>Damage Taken</h3><strong>{total_damage_taken}</strong><span>{html.escape(mvp_risk[0])} survived at {mvp_risk[1]} HP.</span></div>
      <div class="stat"><h3>Support Score <span class="hint" title="{html.escape(support_help)}">?</span></h3><strong>{'N/A' if is_summary_run else total_support}</strong><span>{'Needs combat-event logs.' if is_summary_run else html.escape(mvp_support[0]) + ' led with ' + str(mvp_support[1]) + '.'}</span></div>
      <div class="stat"><h3>Events Parsed</h3><strong>{len(timeline)}</strong><span>Damage, block, status, utility.</span></div>
    </section>

    <h2 class="section-title">Run Leaderboards</h2>
    <section class="grid three">
      {render_leaderboard("Damage Done: N/A" if is_summary_run else "Damage Done", [] if is_summary_run else rank(totals, "damage"))}
      {render_leaderboard("Damage Taken", rank(totals, "damage_taken"))}
      {render_leaderboard("Lowest HP Survived", lowest_hp_rows)}
    </section>

    <h2 class="section-title">Player Breakdown</h2>
    <section class="panel table-panel">
      <table>
        <thead>
          <tr>
            <th>Player</th>
            <th class="number">Damage</th>
            <th class="number">Direct</th>
            <th class="number">Vuln Enabled</th>
            <th class="number">Block</th>
            <th class="number">Taken</th>
            <th class="number">Healing</th>
            <th class="number">Low HP</th>
            <th class="number">Support <span class="hint" title="{html.escape(support_help)}">?</span></th>
            <th>Top Source</th>
          </tr>
        </thead>
        <tbody>{''.join(player_rows)}</tbody>
      </table>
    </section>

    <h2 class="section-title">Acts</h2>
    {''.join(act_sections)}

    <h2 class="section-title">Recent Event Timeline</h2>
    <section class="panel table-panel">
      <table>
        <thead><tr><th>Act</th><th>Turn</th><th>Player</th><th>Type</th><th>Detail</th><th class="number">Amount</th></tr></thead>
        <tbody>{''.join(timeline_rows)}</tbody>
      </table>
    </section>
  </main>
</body>
</html>"""


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate a NeowLogs coop leaderboard HTML report.")
    parser.add_argument("input", type=Path, help="Run event file: .json, .jsonl, or .csv")
    parser.add_argument("-o", "--output", type=Path, default=Path("neowlogs-report.html"), help="HTML output path")
    args = parser.parse_args()

    meta, events = load_events(args.input)
    report = render_report(analyze(meta, events))
    args.output.write_text(report, encoding="utf-8")
    print(f"Wrote {args.output} from {len(events)} events.")


if __name__ == "__main__":
    main()
