"""Estimate common effects in paired, synthetic single-player fights."""

import argparse
import csv
import json
import math
import os
import random
import statistics
from dataclasses import dataclass
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
ATTACK_REFERENCE = 6
PLAYER_HP = 78
MAX_ROUNDS = 24
CASES = (
    (4, "SEWER_CLAM"),
    (8, "AXE_RUBY_RAIDER", "ASSASSIN_RUBY_RAIDER"),
    (12, "BYRDONIS"),
    (18, "BOWLBUG_EGG", "BOWLBUG_NECTAR"),
    (25, "INFESTED_PRISM"),
    (37, "GLOBE_HEAD"),
    (43, "AEONGLASS"),
)
# Each card is (damage per hit, hits, block). All cost one energy.
DECKS = {
    "均衡": [(6, 1, 0)] * 5 + [(9, 1, 0)] * 3 + [(0, 1, 5)] * 4 + [(0, 1, 8)] * 2,
    "多段": [(3, 3, 0)] * 4 + [(5, 2, 0)] * 4 + [(6, 1, 0)] * 2 + [(0, 1, 6)] * 4,
    "防御": [(8, 1, 0)] * 5 + [(5, 2, 0)] * 2 + [(0, 1, 6)] * 4 + [(0, 1, 9)] * 3,
}
PROBES = {
    "Block": (8, "actions", "effectiveAmount", "格挡"),
    "Heal": (8, "actions", "effectiveAmount", "回复"),
    "Strength": (3, "buffs", "amount*attackFrequency", "力量"),
    "Ritual": (1, "buffs", "amount*attackFrequency*survivalTurns", "仪式"),
    "Regen": (5, "buffs", "amount*survivalTurns", "再生"),
    "Plating": (6, "buffs", "amount*survivalTurns", "甲覆"),
    "Weak": (1, "actions", "amount*actionValue", "虚弱（玩家）"),
    "Vulnerable": (1, "actions", "amount*actionValue", "易伤（玩家）"),
    "Frail": (1, "actions", "amount*actionValue", "脆弱（玩家）"),
    "Poison": (2, "actions", "amount", "中毒（玩家）"),
}


@dataclass(frozen=True)
class Move:
    hits: tuple
    block: int


@dataclass(frozen=True)
class Fighter:
    hp: int
    opening: tuple
    cycle: tuple

    def at(self, index):
        if index < len(self.opening):
            return self.opening[index]
        return self.cycle[(index - len(self.opening)) % len(self.cycle)]


@dataclass(frozen=True)
class Outcome:
    lost: int
    rounds: int
    basis: float


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def config_source():
    appdata = os.getenv("APPDATA")
    if appdata:
        game = Path(appdata) / "SlayTheSpire2/mod_data/MonsterIntentRandomizer/config.json"
        if game.is_file():
            return game, game
    return ROOT / "data/settings.json", ROOT / "data/monster-profiles.json"


def reference_fighter(monster, hp):
    moves = {}
    for move in monster["moves"]:
        damage = move.get("damage")
        hits = ((damage["normalPerHit"],) * damage["hits"]
                if damage and damage["normalPerHit"] is not None else ())
        block = sum(effect["amount"] for effect in move["nonAttackEffects"]
                    if effect["kind"] == "block" and effect.get("target", "self") == "self"
                    and type(effect.get("amount")) is int)
        moves[move["id"]] = Move(hits, block)
    cycle = monster["deterministicCycle"]
    return Fighter(hp, tuple(moves[key] for key in cycle["openingBeforeLoop"]),
                   tuple(moves[key] for key in cycle["loop"]))


def fight(fighters, profile, player_action, deck, seed, anchor, probe=None):
    rng = random.Random(seed)
    draw = list(deck)
    rng.shuffle(draw)
    discard = []
    hp = [fighter.hp for fighter in fighters]
    block = [0] * len(fighters)
    player_hp = PLAYER_HP
    strength = ritual = regen = plating = 0
    weak = vulnerable = frail = poison = 0
    basis = 0.0
    round_num = 0
    for turn in range(MAX_ROUNDS):
        if player_hp <= 0 or not any(hp):
            break
        round_num += 1
        player_hp = max(0, player_hp - poison)
        poison = max(0, poison - 1)
        if player_hp == 0:
            break

        moves = [fighter.at(turn) for fighter in fighters]
        extra = ATTACK_REFERENCE if probe == "Reference" and turn == anchor else 0
        known_hits = []
        for enemy, move in enumerate(moves):
            added, remainder = divmod(extra if enemy == 0 else 0, len(move.hits) or 1)
            known_hits.append(tuple(damage + (strength if enemy == 0 else 0) + added + (i < remainder)
                                    for i, damage in enumerate(move.hits)))
        hand = []
        for _ in range(5):
            if not draw:
                draw, discard = discard, []
                rng.shuffle(draw)
            hand.append(draw.pop())
        player_block = 0
        starting_hp = hp.copy()
        starting_block = block[0]

        for _ in range(3):
            live = [i for i, life in enumerate(hp) if life]
            if not live:
                break
            # Focus the marked monster while alive. Re-evaluate lethal and block need after every card.
            target = live[0]
            incoming = sum(math.floor(damage * (1.5 if vulnerable else 1))
                           for enemy in live for damage in known_hits[enemy])
            needed = max(0, incoming - player_block)
            options = []
            for index, (raw, hits, guard) in enumerate(hand):
                damage = math.floor(raw * (0.75 if weak else 1)) * hits
                guard = math.floor(guard * (0.75 if frail else 1))
                target_hp = hp[target] + block[target]
                utility = min(damage, target_hp) + 1.18 * min(needed, guard)
                if damage >= target_hp:
                    utility += 9 + 0.75 * sum(known_hits[target])
                options.append((utility, index, damage, guard))
            utility, index, damage, guard = max(options, key=lambda option: option[0])
            if utility <= 0:
                break
            discard.append(hand.pop(index))
            player_block += guard
            hp[target] = max(0, hp[target] - max(0, damage - block[target]))
            block[target] = max(0, block[target] - damage)
        discard.extend(hand)

        for enemy, (move, hits) in enumerate(zip(moves, known_hits)):
            if hp[enemy] == 0 or player_hp == 0:
                continue
            block[enemy] = move.block
            for base in hits:
                damage = math.floor(base * (1.5 if vulnerable else 1))
                player_hp = max(0, player_hp - max(0, damage - player_block))
                player_block = max(0, player_block - damage)
            if enemy != 0 or player_hp == 0:
                continue
            if turn:
                plating = max(0, plating - 1)
            applied = turn == anchor and probe in PROBES
            if applied:
                amount = PROBES[probe][0]
                survival = (starting_hp[0] + sum(starting_hp[1:]) / 2) / player_action
                frequency = profile["attackFrequency"]
                if probe == "Block":
                    block[0] += amount
                    basis = min(amount, max(0, player_action / sum(life > 0 for life in starting_hp) - starting_block))
                elif probe == "Heal":
                    basis = min(amount, fighters[0].hp - hp[0])
                    hp[0] += basis
                elif probe == "Strength":
                    strength += amount
                    basis = amount * frequency
                elif probe == "Ritual":
                    ritual += amount
                    basis = amount * frequency * survival
                elif probe in ("Regen", "Plating"):
                    regen += amount if probe == "Regen" else 0
                    plating += amount if probe == "Plating" else 0
                    basis = amount * survival
                elif probe == "Poison":
                    poison += amount
                    basis = amount
                else:
                    weak += amount if probe == "Weak" else 0
                    vulnerable += amount if probe == "Vulnerable" else 0
                    frail += amount if probe == "Frail" else 0
                    basis = amount * profile["actionValue"]
            hp[0] = min(fighters[0].hp, hp[0] + regen)
            block[0] += plating
            regen = max(0, regen - 1)
            if not (applied and probe == "Ritual"):
                strength += ritual
        for effect in ("Weak", "Vulnerable", "Frail"):
            if not (turn == anchor and probe == effect):
                if effect == "Weak":
                    weak = max(0, weak - 1)
                elif effect == "Vulnerable":
                    vulnerable = max(0, vulnerable - 1)
                else:
                    frail = max(0, frail - 1)
    return Outcome(PLAYER_HP - player_hp, round_num, basis)


def run(settings, profiles, seeds):
    roster = {entry["id"]: entry for entry in load(ROOT / "data/monster-action-baselines.json")["monsters"]}
    curve = settings["playerAction"]
    rows = []
    for rooms, *ids in CASES:
        profile = profiles["monsters"][ids[0]]
        fighters = [reference_fighter(roster[key], profiles["monsters"][key]["hp"]) for key in ids]
        anchor = next(turn for turn in range(len(fighters[0].opening) + len(fighters[0].cycle))
                      if fighters[0].at(turn).hits)
        player_action = (curve["base"] + rooms / curve["roomsPerStep"]) ** curve["exponent"]
        for deck_name, source in DECKS.items():
            scale = player_action / 20
            deck = [(round(damage * scale), hits, round(block * scale)) for damage, hits, block in source]

            def play(seed, probe=None):
                return fight(fighters, profile, player_action, deck, seed, anchor, probe)

            baselines = [play(seed) for seed in range(seeds)]
            references = [play(seed, "Reference") for seed in range(seeds)]
            for probe, (amount, group, term, label) in PROBES.items():
                pairs = [(base, ref, with_effect) for seed, (base, ref) in enumerate(zip(baselines, references))
                         if (with_effect := play(seed, probe)).basis > 0]
                if not pairs:
                    continue
                delta = statistics.mean(effect.lost - base.lost for base, _, effect in pairs)
                benchmark = statistics.mean(ref.lost - base.lost for base, ref, _ in pairs)
                basis = statistics.mean(effect.basis for _, _, effect in pairs)
                equivalent = ATTACK_REFERENCE * delta / benchmark if benchmark >= 0.5 else None
                current = (settings["actions"]["valuation"][probe] if group == "actions"
                           else settings["buffs"][probe]["formula"])[term]
                rows.append({
                    "monster": ids[0], "encounter": "+".join(ids), "rooms": rooms, "deck": deck_name,
                    "effect": probe, "label": label, "amount": amount, "pairs": len(pairs),
                    "hp_delta": round(delta, 4), "reference_hp_delta": round(benchmark, 4),
                    "rounds_delta": round(statistics.mean(effect.rounds - base.rounds for base, _, effect in pairs), 4),
                    "configured_value": round(current * basis, 4), "equivalent": equivalent,
                    "configured_coefficient": current, "observed_coefficient": None if equivalent is None else equivalent / basis,
                })
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, help="游戏保存的 config.json")
    parser.add_argument("--seeds", type=int, default=384)
    parser.add_argument("--output", type=Path, default=ROOT / "dist/attack-calibration/common-effects.csv")
    args = parser.parse_args()
    if args.seeds < 1:
        parser.error("--seeds must be positive.")
    settings_file, profiles_file = (args.config, args.config) if args.config else config_source()
    settings_doc, profiles_doc = load(settings_file), load(profiles_file)
    rows = run(settings_doc.get("Settings", settings_doc), profiles_doc.get("Profiles", profiles_doc), args.seeds)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=rows[0])
        writer.writeheader()
        writer.writerows(rows)
    print(settings_file)
    print("能力 | 配置系数 / 样本中位数 （各组范围，有效组数）")
    for probe, (_, _, _, label) in PROBES.items():
        candidates = [row for row in rows if row["effect"] == probe]
        valid = [row for row in candidates if row["observed_coefficient"] is not None and row["pairs"] >= 24]
        if valid:
            values = [row["observed_coefficient"] for row in valid]
            print(f"{label:10} {valid[0]['configured_coefficient']:.3f} / {statistics.median(values):.3f} "
                  f"({min(values):.3f} – {max(values):.3f}, {len(valid)}/{len(candidates)} 组)")
    print(args.output)


if __name__ == "__main__":
    main()
