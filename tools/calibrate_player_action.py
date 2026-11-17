"""Estimate player action value against a neutral target with example decks."""

import argparse
import json
import os
import random
import statistics
from dataclasses import dataclass
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
FLOORS = (4, 10, 16, 22, 28, 34, 40, 46)
TURNS = 3
SHUFFLES = 384


@dataclass(frozen=True)
class Card:
    cost: int = 1
    damage: int = 0
    hits: int = 1
    draw: int = 0
    energy: int = 0
    strength: int = 0
    exhaust: bool = False
    block: int = 0
    vulnerable: int = 0


BASIC_ATTACK = Card(damage=6)
DEFENSE = Card(block=5)
STARTER = [BASIC_ATTACK] * 5 + [DEFENSE] * 4 + [Card(cost=2, damage=12)]

# Same development checkpoints for each deck. Values are deliberately specified independently
# of the existing player-action curve. Cards are numerical examples, not class-specific builds.
ADDITIONS = {
    "直接攻击": (
        [Card(damage=9), Card(damage=10)],
        [Card(cost=2, damage=22), Card(damage=13)],
        [Card(cost=0, damage=5), Card(damage=16)],
        [Card(cost=2, damage=29), Card(damage=14, draw=1)],
        [Card(damage=20), Card(cost=0, damage=8)],
        [Card(cost=2, damage=34), Card(damage=18)],
        [Card(damage=22), Card(cost=1, draw=3)],
        [Card(cost=2, damage=38), Card(damage=20, draw=1)],
    ),
    "多段成长": (
        [Card(damage=4, hits=2), Card(damage=8)],
        [Card(damage=4, hits=3), Card(strength=2, exhaust=True)],
        [Card(cost=2, damage=5, hits=4), Card(cost=0, damage=3, hits=2)],
        [Card(strength=3, exhaust=True), Card(damage=5, hits=3)],
        [Card(cost=2, damage=6, hits=4), Card(damage=4, hits=3, draw=1)],
        [Card(strength=4, exhaust=True), Card(cost=0, damage=4, hits=3)],
        [Card(damage=6, hits=4), Card(damage=4, hits=3, draw=1)],
        [Card(cost=2, damage=8, hits=5), Card(strength=4, exhaust=True)],
    ),
    "过牌加费": (
        [Card(damage=8, draw=1), Card(damage=10)],
        [Card(cost=0, damage=5, draw=1), Card(draw=3)],
        [Card(cost=0, energy=2, exhaust=True), Card(damage=13, draw=1)],
        [Card(cost=2, damage=24), Card(cost=0, damage=6)],
        [Card(cost=0, draw=2), Card(damage=15, draw=1)],
        [Card(cost=0, energy=2, draw=1, exhaust=True), Card(damage=17)],
        [Card(cost=0, damage=8, draw=1), Card(cost=2, damage=33)],
        [Card(damage=18, draw=1), Card(draw=4)],
    ),
}


def build_deck(name, floor):
    deck = STARTER[:]
    for index, checkpoint in enumerate(FLOORS):
        if checkpoint > floor:
            break
        deck.extend(ADDITIONS[name][index])
        if index in (1, 3, 5):
            deck.remove(BASIC_ATTACK)
        if index in (2, 4, 6):
            deck.remove(DEFENSE)
    return deck


def draw_cards(number, draw, discard, hand, rng):
    for _ in range(number):
        if not draw:
            if not discard:
                return
            draw.extend(discard)
            discard.clear()
            rng.shuffle(draw)
        hand.append(draw.pop())


def attack_dummy(deck, seed):
    rng = random.Random(seed)
    draw = list(deck)
    rng.shuffle(draw)
    discard = []
    strength = 0
    damage = 0
    for turn in range(TURNS):
        hand, played = [], []
        energy = 3
        draw_cards(5, draw, discard, hand, rng)
        while hand:
            def priority(card):
                if card.cost > energy:
                    return 0
                hit = (card.damage + strength) * card.hits if card.damage else 0
                growth = card.strength * (TURNS - turn) * 2
                return (hit + growth + card.draw * 4 + card.energy * 6) / max(1, card.cost)

            card = max(hand, key=priority)
            if priority(card) <= 0:
                break
            hand.remove(card)
            energy += card.energy - card.cost
            if card.damage:
                damage += (card.damage + strength) * card.hits
            strength += card.strength
            draw_cards(card.draw, draw, discard, hand, rng)
            if not card.exhaust:
                played.append(card)
        discard.extend(played)
        discard.extend(hand)
    return damage / TURNS


def load_current():
    appdata = os.getenv("APPDATA")
    if appdata:
        file = Path(appdata) / "SlayTheSpire2/mod_data/MonsterIntentRandomizer/config.json"
        if file.is_file():
            return json.loads(file.read_text(encoding="utf-8-sig"))["Settings"]["playerAction"]
    return json.loads((ROOT / "data/settings.json").read_text(encoding="utf-8-sig"))["playerAction"]


def fit_curve(floors, central, exponent):
    # Fit the base and room scaling while preserving the existing exponent.
    transformed = [value ** (1 / exponent) for value in central]
    x, y = statistics.mean(floors), statistics.mean(transformed)
    slope = sum((floor - x) * (value - y) for floor, value in zip(floors, transformed)) / sum(
        (floor - x) ** 2 for floor in floors)
    return {"base": y - slope * x, "roomsPerStep": 1 / slope, "exponent": exponent}


def predict(curve, floor):
    return (curve["base"] + floor / curve["roomsPerStep"]) ** curve["exponent"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "dist/attack-calibration/player-action-curve.json")
    args = parser.parse_args()
    current = load_current()
    rows = []
    for floor in FLOORS:
        means = {}
        for name in ADDITIONS:
            deck = build_deck(name, floor)
            means[name] = statistics.mean(attack_dummy(deck, seed) for seed in range(SHUFFLES))
        rows.append({"floor": floor, "measured": {key: round(value, 2) for key, value in means.items()},
                     "median_between_builds": round(statistics.median(means.values()), 2),
                     "current_curve": round(predict(current, floor), 2)})
    fitted = fit_curve(FLOORS, [row["median_between_builds"] for row in rows], current["exponent"])
    for row in rows:
        row["trial_curve"] = round(predict(fitted, row["floor"]), 2)
    result = {
        "metric": f"前 {TURNS} 回合打高血量、无状态、无格挡靶怪，每回合攻击伤害",
        "model": "三个合成发育路线，每档加入两张牌并在部分楼层移除初始牌。五张手牌、三点初始能量，模拟出牌、力量、抽牌和加费。",
        "shuffles_per_build_floor": SHUFFLES, "current": current, "fitted": fitted, "floors": rows,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
