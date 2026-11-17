"""Measure how much a fixed attack increment realizes at different enemy turns."""

import argparse
import json
import math
import statistics
from pathlib import Path

from calibrate_action_values import (
    ADDITIONS, ATTACK_REFERENCE, Fight, MULTI_CYCLES, Probe, ROOT, build_deck, config_source, load,
    reference_fighter, scenes,
)


class MeasuredFight(Fight):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.can_attack = []

    def player_turn(self, packets):
        super().player_turn(packets)
        self.can_attack.append(bool(self.player_hp and self.hp[self.actor] and packets[self.actor]))

    def reached(self, turn):
        return turn < len(self.can_attack) and self.can_attack[turn]


def measure(profiles, seeds, turns):
    roster = {monster["id"]: monster for monster in load(ROOT / "data/monster-action-baselines.json")["monsters"]}
    for key, loop in MULTI_CYCLES.items():
        roster[key]["deterministicCycle"] = {"openingBeforeLoop": [], "loop": loop}
    # One action order per encounter. The extra attack and baseline still share the same order.
    cases = [case for case in (*scenes(False), *scenes(True)) if case[-1] == "original"]
    for rooms, encounter, ids, actor, priority, _ in cases:
        fighters = [reference_fighter(roster[key], profiles["monsters"][key]["hp"]) for key in ids]
        attack_turns = [turn for turn in range(turns) if fighters[actor].at(turn).hits]
        if not attack_turns or attack_turns[0] != 0:
            continue
        for build in ADDITIONS:
            deck = build_deck(build, rooms)

            def fight(seed, turn=0, probe=None):
                return MeasuredFight(fighters, deck, seed, turn, probe, actor, priority)

            baselines = []
            for seed in range(seeds):
                control = fight(seed)
                loss, *_ = control.run()
                baselines.append((control, loss))
            for turn in attack_turns:
                deltas = []
                reached_deltas = []
                for seed, (control, before) in enumerate(baselines):
                    after, *_ = fight(seed, turn, Probe("Attack", ATTACK_REFERENCE)).run()
                    delta = after - before
                    deltas.append(delta)
                    if control.reached(turn):
                        reached_deltas.append(delta)
                yield {
                    "encounter": encounter, "actor": ids[actor], "n": rooms, "build": build, "turn": turn + 1,
                    "scheduled_seeds": seeds, "reached_seeds": len(reached_deltas),
                    "hp_delta": statistics.mean(deltas),
                    "hp_delta_if_reached": statistics.mean(reached_deltas) if reached_deltas else None,
                }


def summarize(rows, last_turn):
    baseline = {(row["encounter"], row["build"]): row for row in rows if row["turn"] == 1}
    table = []
    for turn in range(1, last_turn + 1):
        paired = [(baseline[row["encounter"], row["build"]], row) for row in rows if row["turn"] == turn]
        paired = [(first, later) for first, later in paired
                  if first["hp_delta"] >= 0.6 and first["reached_seeds"] >= 16]
        if not paired:
            continue
        first_hp = sum(first["hp_delta"] for first, _ in paired)
        later_hp = sum(later["hp_delta"] for _, later in paired)
        first_reached = sum(first["reached_seeds"] for first, _ in paired)
        later_reached = sum(later["reached_seeds"] for _, later in paired)
        realized_ratio = later_hp / first_hp
        conditional = [(first, later) for first, later in paired if later["reached_seeds"] >= 16]
        first_risk = sum(r["hp_delta_if_reached"] * r["reached_seeds"] for r, _ in conditional)
        late_risk = sum(r["hp_delta_if_reached"] * r["reached_seeds"] for _, r in conditional)
        first_opportunities = sum(r["reached_seeds"] for r, _ in conditional)
        later_opportunities = sum(r["reached_seeds"] for _, r in conditional)
        conditional_ratio = None
        if later_opportunities and first_risk > 0:
            conditional_ratio = (late_risk / later_opportunities) / (first_risk / first_opportunities)
        table.append({
            "turn": turn, "matched_groups": len(paired), "conditional_groups": len(conditional),
            "opportunity_ratio": later_reached / first_reached,
            "realized_hp_ratio": realized_ratio, "conditional_hp_ratio": conditional_ratio,
        })
    return table


def geometric_divisor(rows, ratio_key, group_key):
    data = [(row["turn"] - 1, row[ratio_key], row[group_key]) for row in rows
            if row["turn"] > 1 and row[group_key] >= 5 and row[ratio_key] is not None and row[ratio_key] > 0]
    if not data:
        return None
    exponent = sum(n * math.log(ratio) * count for n, ratio, count in data) / sum(n * n * count for n, _, count in data)
    return math.exp(-exponent)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--seeds", type=int, default=256)
    parser.add_argument("--turns", type=int, default=6)
    parser.add_argument("--output", type=Path, default=ROOT / "dist/attack-calibration/late-action-discount.json")
    args = parser.parse_args()
    if args.seeds < 16 or args.turns < 2:
        parser.error("Use at least 16 seeds and two turns.")
    _, path = config_source()
    document = load(path)
    profiles = document.get("Profiles", document)
    measured = list(measure(profiles, args.seeds, args.turns))
    summary = summarize(measured, args.turns)
    result = {
        "reference": "A single scheduled enemy attack deals six more base damage. Each variant uses the same deck, seed and enemy actions.",
        "seed_count": args.seeds,
        "total_divisor": geometric_divisor(summary, "realized_hp_ratio", "matched_groups"),
        "conditional_divisor": geometric_divisor(summary, "conditional_hp_ratio", "conditional_groups"),
        "summary": summary, "measured": measured,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in result.items() if k != "measured"}, ensure_ascii=False, indent=2))
    print(args.output)


if __name__ == "__main__":
    main()
