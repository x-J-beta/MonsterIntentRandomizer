"""Paired extra-action calibration for the configured combat effects."""

import argparse
import csv
import json
import math
import random
import statistics
from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path

from calibrate_common_effects import ATTACK_REFERENCE, CASES, MAX_ROUNDS, ROOT, config_source, load, reference_fighter
from calibrate_player_action import ADDITIONS, BASIC_ATTACK, Card, build_deck, draw_cards, predict


EXCLUDED = {"Debt", "PaperCuts"}
DEATH_PHASE_EFFECT = "SteamEruption"
GENERATED = {"PersonalHive": ("Dazed", "draw"), "WitheringPresence": ("Wither", "hand"),
             "PainfulStabs": ("Wound", "discard")}


@dataclass(frozen=True)
class Status:
    cost: int = -1
    damage: int = 0
    hp_loss: int = 0
    exhaust: bool = False
    ethereal: bool = False
    retained: bool = False


CARDS = {
    "Dazed": Status(ethereal=True), "Wound": Status(), "Burn": Status(damage=2),
    "Beckon": Status(cost=1, hp_loss=6), "Wither": Status(damage=3), "Amnesia": Status(),
    "MindRot": Status(), "Sadness": Status(retained=True), "Infection": Status(damage=3),
    "Sloth": Status(), "Toxic": Status(cost=1, damage=5, exhaust=True), "Soot": Status(),
    "Disintegration": Status(), "Debris": Status(cost=1, exhaust=True), "Void": Status(ethereal=True),
    "WasteAway": Status(), "Slimed": Status(cost=1, exhaust=True), "Enthralled": Status(cost=2),
    "Normality": Status(), "BadLuck": Status(hp_loss=13),
}

DOSES = {
    "Block": (5, 9, 14), "Heal": (5, 9, 14), "Strength": (1, 3, 5), "Ritual": (1, 2, 3),
    "Regen": (5, 8, 12), "Plating": (6, 9, 13), "Weak": (1, 2, 3), "Vulnerable": (1, 2, 3),
    "Frail": (1, 2, 3), "Poison": (2, 4, 7), "Tangled": (1, 2, 3), "Soulbind": (1, 2, 3),
    "DrawDown": (1, 2, 3), "EnergyDown": (1, 2, 4), "StrengthDown": (1, 2, 4),
    "DexterityDown": (1, 2, 4), "Hex": (2,), "Intangible": (1,), "Artifact": (1, 2),
    "Thorns": (1, 3, 5), "TemporaryThorns": (1, 3, 5), "Slippery": (1, 2, 4),
    "Vigor": (3, 5, 8), "Enrage": (1, 2, 3), "PersonalHive": (1, 2), "VitalSpark": (1, 2, 3),
    "WitheringPresence": (3, 5, 8), "Envenom": (1, 2, 3), "PainfulStabs": (1, 2),
    "HardenedShell": (0.35, 0.65, 1.0), "HardToKill": (0.15, 0.3, 0.5),
}
TEMPORARY = ("Weak", "Vulnerable", "Frail", "Tangled", "Ringing", "Intangible")
CARD_APPLICATIONS = {
    "Void": ("EnergyDown", 1), "Amnesia": ("Amnesia", 1), "MindRot": ("MindRot", 1),
    "Sloth": ("Sloth", 3), "Disintegration": ("Disintegration", 6), "WasteAway": ("WasteAway", 1),
}
MULTI_CASES = (
    (8, "RUBY_RAIDERS_NORMAL", ("AXE_RUBY_RAIDER", "ASSASSIN_RUBY_RAIDER", "CROSSBOW_RUBY_RAIDER"), 2),
    (11, "CULTISTS_NORMAL", ("CALCIFIED_CULTIST", "DAMP_CULTIST"), 1),
    (14, "SEAPUNK_NORMAL", ("CALCIFIED_CULTIST", "SEAPUNK"), 1),
    (22, "BOWLBUG_PAIR", ("BOWLBUG_EGG", "BOWLBUG_NECTAR"), 1),
    (38, "CONSTRUCT_MENAGERIE_NORMAL", ("PUNCH_CONSTRUCT", "CUBEX_CONSTRUCT", "CUBEX_CONSTRUCT"), 0),
)
MULTI_CYCLES = {
    "CROSSBOW_RUBY_RAIDER": ("RELOAD", "FIRE"),
    "PUNCH_CONSTRUCT": ("READY", "FAST_PUNCH", "STRONG_PUNCH"),
}
MULTI_PROBES = {
    "Poison", "Vulnerable", "Weak", "Frail", "Strength", "Ritual", "Block", "Heal", "Regen", "Plating",
    "Thorns", "TemporaryThorns", "Intangible", "Soulbind", "DrawDown", "EnergyDown", "Envenom",
    "PainfulStabs", "PersonalHive", "WitheringPresence", "Wound", "Burn", "Toxic", "Void",
}


@dataclass(frozen=True)
class Probe:
    name: str
    amount: int
    pile: str = ""
    generated: str = ""


class Fight:
    def __init__(self, fighters, deck, seed, anchor, probe, actor=0, target_order=None, toxic_priority=4,
                 respect_thorns=True, avoid_temporary_thorns=False):
        self.fighters = fighters
        self.actor = actor
        self.target_order = range(len(fighters)) if target_order is None else target_order
        self.toxic_priority = toxic_priority
        self.respect_thorns = respect_thorns
        self.avoid_temporary_thorns = avoid_temporary_thorns
        self.hp = [fighter.hp for fighter in fighters]
        self.guard = [0] * len(fighters)
        self.player_hp = 78
        self.block = 0
        self.strength = 0
        self.dexterity = 0
        self.energy = 0
        self.tainted = 0
        self.taken = 0
        self.powers = Counter()
        self.draw = deck[:]
        self.discard, self.hand, self.played_cards = [], [], []
        self.rng = random.Random(seed)
        self.insert_rng = random.Random(seed ^ 0x2589AC)
        self.rng.shuffle(self.draw)
        self.anchor = anchor
        self.probe = probe
        self.enemy_vulnerable = [0] * len(fighters)
        self.generator_count = 0
        self.bound_drawn = 0
        self.generated = 0
        self.reflection_hits = 0
        self.reflection_hp = 0
        self.effective_amount = None
        self.quote = None

    def insert(self, name, count, pile):
        if not count:
            return
        if pile == "top":
            self.draw.extend([name] * count)
        elif pile == "draw":
            for _ in range(count):
                self.draw.insert(self.insert_rng.randrange(len(self.draw) + 1), name)
        elif pile == "discard":
            self.discard.extend([name] * count)
        else:
            self.hand.extend((name, False) for _ in range(count))

    def draw_cards(self, number):
        for _ in range(max(0, number)):
            if not self.draw:
                if not self.discard:
                    break
                self.draw, self.discard = self.discard, []
                self.rng.shuffle(self.draw)
            card = self.draw.pop()
            if isinstance(card, str) and card in CARD_APPLICATIONS:
                power, amount = CARD_APPLICATIONS[card]
                if card == "Void":
                    self.energy = max(0, self.energy - amount)
                else:
                    self.powers[power] += amount
                    if card in ("Amnesia", "WasteAway"):
                        self.energy = max(0, self.energy - 1)
                    continue
            bind = self.bound_drawn < self.powers["Soulbind"] and isinstance(card, Card)
            if bind:
                self.bound_drawn += 1
            self.hand.append((card, bind))

    def hurt_player(self, amount, unblockable=False):
        if not unblockable:
            blocked = min(self.block, amount)
            self.block -= blocked
            amount -= blocked
        self.player_hp = max(0, self.player_hp - amount)
        return amount

    def hurt_enemy(self, enemy, amount):
        if enemy == self.actor:
            thorns = self.powers["Thorns"] + self.powers["TemporaryThorns"]
            if thorns:
                before = self.player_hp
                self.hurt_player(thorns)
                self.reflection_hits += 1
                self.reflection_hp += before - self.player_hp
        if enemy == self.actor and self.powers["HardToKill"]:
            amount = min(amount, self.powers["HardToKill"])
        blocked = min(self.guard[enemy], amount)
        self.guard[enemy] -= blocked
        loss = amount - blocked
        if enemy == self.actor:
            if self.powers["HardenedShell"]:
                loss = min(loss, max(0, self.powers["HardenedShell"] - self.taken))
            if loss > 0 and self.powers["Slippery"]:
                self.powers["Slippery"] -= 1
                loss = 1
            if self.powers["Intangible"]:
                loss = min(1, loss)
            self.taken += loss
        self.hp[enemy] = max(0, self.hp[enemy] - loss)
        if enemy == self.actor:
            self.generate("PersonalHive", self.powers["PersonalHive"])

    def generate(self, power, count):
        if count and self.probe and self.probe.name == power:
            card, pile = GENERATED[power]
            self.insert(self.probe.generated or card, count, pile)
            self.generated += count

    def packets(self, turn, moves):
        extra = ATTACK_REFERENCE if turn == self.anchor and self.probe and self.probe.name == "Attack" else 0
        packets = []
        for enemy, move in enumerate(moves):
            vigor = self.powers["Vigor"] if enemy == self.actor else 0
            strength = self.powers["Strength"] if enemy == self.actor else 0
            added, remainder = divmod(extra if enemy == self.actor else 0, len(move.hits) or 1)
            packets.append(tuple(hit + vigor + strength + added + (index < remainder)
                                 for index, hit in enumerate(move.hits)))
        return packets

    def stats(self, card):
        cost = card.cost + (1 if card.damage and self.powers["Tangled"] else 0)
        shrink = 0.7 if self.powers["Shrink"] else 1
        weakness = 0.75 if self.powers["Weak"] else 1
        frail = 0.75 if self.powers["Frail"] else 1
        dampen = bool(self.powers["Dampen"] and self.hp[self.actor])
        damage = math.floor(max(0, card.damage + self.strength - 2 * dampen) * shrink * weakness) if card.damage else 0
        block = math.floor(max(0, card.block + self.dexterity - 2 * dampen) * frail) if card.block else 0
        return cost, damage, block

    def player_turn(self, packets):
        self.block = 0
        self.taken = 0
        self.tainted = 0
        plays = skills = 0
        used_bound = False
        self.bound_drawn = 0
        self.hurt_player(self.powers["Poison"], unblockable=True)
        self.powers["Poison"] = max(0, self.powers["Poison"] - 1)
        self.energy = max(0, 3 - self.powers["EnergyDown"] - self.powers["WasteAway"] - self.powers["Amnesia"])
        self.powers["EnergyDown"] = max(0, self.powers["EnergyDown"] - 3)
        draw_count = max(0, 5 - self.powers["DrawDown"] - self.powers["MindRot"] - 2 * self.powers["Amnesia"])
        self.powers["DrawDown"] = max(0, self.powers["DrawDown"] - 5)
        self.draw_cards(draw_count)
        while self.hand and self.player_hp and any(self.hp):
            if (self.powers["Ringing"] and plays or self.powers["Sloth"] and plays >= self.powers["Sloth"]
                    or plays >= 3 and any(card == "Normality" for card, _ in self.hand)):
                break
            targets = [i for i in self.target_order if self.hp[i]]
            target = targets[0]
            thorns = self.powers["Thorns"] + self.powers["TemporaryThorns"] if self.respect_thorns and self.hp[self.actor] else 0
            incoming = sum(hit for enemy, hits in enumerate(packets) if self.hp[enemy] for hit in hits)
            incoming *= 1.5 if self.powers["Vulnerable"] else 1
            needed = max(0, incoming - self.block)
            enthralled = any(card == "Enthralled" for card, _ in self.hand)
            best = None
            for index, (card, bound) in enumerate(self.hand):
                if enthralled and card != "Enthralled" or bound and used_bound:
                    continue
                if isinstance(card, str):
                    status = CARDS[card]
                    cost = status.cost
                    value = {"Enthralled": 20, "Beckon": 5, "Toxic": self.toxic_priority, "Slimed": 3}.get(card, 0)
                    value /= max(1, cost)
                    if 0 <= cost <= self.energy and value > 0 and (best is None or value > best[0]):
                        best = value, index, cost, 0, 0, target
                    continue
                if self.powers["Smoggy"] and skills and not card.damage and not card.strength:
                    continue
                cost, damage, block = self.stats(card)
                if cost > self.energy:
                    continue
                for defender in targets if thorns and damage else (target,):
                    target_hp = self.hp[defender] + self.guard[defender]
                    hit = math.floor(damage * (1.5 if self.enemy_vulnerable[defender] else 1))
                    attack = min(hit * card.hits, target_hp)
                    lethal = hit * card.hits >= target_hp and not (defender == self.actor and
                        any(self.powers[key] for key in ("HardenedShell", "HardToKill", "Intangible", "Slippery")))
                    value = attack + 1.10 * min(needed, block) + 2.2 * card.draw + 4 * card.energy
                    value += card.strength * min(6, target_hp / 12) + 2 * card.vulnerable
                    if lethal:
                        value += 10 + sum(packets[defender])
                    if thorns and defender == self.actor and hit:
                        hits = min(card.hits, math.ceil(target_hp / hit)) if lethal else card.hits
                        reflected = thorns * hits
                        guard = self.block + block
                        if reflected >= self.player_hp + guard:
                            continue
                        danger = max(0, incoming - (sum(packets[defender]) if lethal else 0))
                        extra_loss = max(0, danger + reflected - guard) - max(0, danger - guard)
                        if self.avoid_temporary_thorns and self.powers["TemporaryThorns"] and extra_loss > 0:
                            continue
                        value -= 1.10 * extra_loss
                    value /= max(1, cost)
                    if value > 0 and (best is None or value > best[0]):
                        best = value, index, cost, damage, block, defender
            if best is None:
                break
            _, index, cost, damage, block, target = best
            card, bound = self.hand.pop(index)
            self.energy -= cost
            used_bound |= bound
            plays += 1
            if isinstance(card, str):
                if card == "Slimed":
                    self.draw_cards(1)
                if not CARDS[card].exhaust:
                    self.played_cards.append(card)
            else:
                self.block += block
                for _ in range(card.hits if damage else 0):
                    if not self.hp[target] or not self.player_hp:
                        break
                    self.hurt_enemy(target, math.floor(damage * (1.5 if self.enemy_vulnerable[target] else 1)))
                if not card.damage and not card.strength:
                    skills += 1
                    if self.hp[self.actor]:
                        self.powers["Strength"] += self.powers["Enrage"]
                        self.tainted += self.powers["VitalSpark"]
                if card.vulnerable and self.hp[target]:
                    if target == self.actor and self.powers["Artifact"]:
                        self.powers["Artifact"] -= 1
                    else:
                        self.enemy_vulnerable[target] += card.vulnerable
                self.strength += card.strength
                self.energy += card.energy
                self.draw_cards(card.draw)
                if not card.exhaust:
                    self.played_cards.append(card)
            if self.powers["WitheringPresence"] and self.hp[self.actor]:
                self.generator_count += 1
                if self.generator_count >= self.powers["WitheringPresence"]:
                    self.generate("WitheringPresence", 1)
                    self.generator_count = 0
            if self.powers["Tender"]:
                self.strength -= 1
                self.dexterity -= 1
        if self.powers["Tender"]:
            self.strength += plays
            self.dexterity += plays
        if any(self.hp):
            self.hurt_player(self.powers["Disintegration"])
        kept = []
        for card, _ in self.hand:
            if isinstance(card, str):
                spec = CARDS[card]
                if any(self.hp):
                    self.hurt_player(spec.damage)
                    self.hurt_player(spec.hp_loss, unblockable=True)
                if spec.retained:
                    kept.append((card, False))
                elif not spec.ethereal:
                    self.discard.append(card)
            elif not (self.powers["Hex"] and self.hp[self.actor]):
                self.discard.append(card)
        self.discard.extend(self.played_cards)
        self.hand, self.played_cards = kept, []
        self.powers["Amnesia"] = 0

    def apply_probe(self, enemy_hp):
        if self.probe is None or self.probe.name == "Attack":
            return
        name, amount = self.probe.name, self.probe.amount
        self.effective_amount = amount
        if name == "Block":
            self.guard[self.actor] += amount
        elif name == "Heal":
            self.effective_amount = min(amount, enemy_hp - self.hp[self.actor])
            self.hp[self.actor] += self.effective_amount
        elif name in CARDS:
            self.insert(name, amount, self.probe.pile)
        elif name == "StrengthDown":
            self.strength -= amount
        elif name == "DexterityDown":
            self.dexterity -= amount
        else:
            self.powers[name] += amount

    def run(self):
        rounds = 0
        for turn in range(MAX_ROUNDS):
            if not self.player_hp or not any(self.hp):
                break
            rounds += 1
            moves = [fighter.at(turn) for fighter in self.fighters]
            if turn == self.anchor:
                hp = self.hp[self.actor]
                allies = sum(self.hp) - hp
                living = sum(value > 0 for value in self.hp)
                behind = sum(value > 0 for value in self.hp[self.actor + 1:])
                self.quote = (hp, allies, self.guard[self.actor], living, behind)
            self.player_turn(self.packets(turn, moves))
            self.powers["TemporaryThorns"] = 0
            packets = self.packets(turn, moves)
            applied = None
            for enemy, (fighter, move, hits) in enumerate(zip(self.fighters, moves, packets)):
                if not self.hp[enemy] or not self.player_hp:
                    continue
                self.guard[enemy] = move.block + (self.guard[enemy] if enemy == self.actor and self.powers["Barricade"] else 0)
                landed = sum(self.hurt_player(math.floor((hit + self.tainted) *
                             (1.5 if self.powers["Vulnerable"] else 1))) > 0 for hit in hits)
                if enemy == self.actor:
                    if hits:
                        self.powers["Vigor"] = 0
                    self.powers["Poison"] += landed * self.powers["Envenom"]
                    self.generate("PainfulStabs", landed * self.powers["PainfulStabs"])
                    self.powers["Plating"] = max(0, self.powers["Plating"] - 1)
                    if turn == self.anchor and self.player_hp:
                        self.apply_probe(fighter.hp)
                        applied = self.probe.name if self.probe else None
                    self.guard[enemy] += self.powers["Plating"]
                    self.hp[enemy] = min(fighter.hp, self.hp[enemy] + self.powers["Regen"])
                    self.powers["Regen"] = max(0, self.powers["Regen"] - 1)
                    if applied != "Ritual":
                        self.powers["Strength"] += self.powers["Ritual"]
            for key in TEMPORARY:
                if key != applied:
                    self.powers[key] = max(0, self.powers[key] - 1)
            self.enemy_vulnerable = [max(0, value - 1) for value in self.enemy_vulnerable]
        return 78 - self.player_hp, rounds, self.effective_amount, self.generated


def recipes(settings, hp, survival):
    actions = settings["actions"]
    for name in [*actions["valuation"], *settings["buffs"], *actions["statusCardValues"]]:
        if name in EXCLUDED or name in ("Attack", DEATH_PHASE_EFFECT):
            continue
        if name in ("HardenedShell", "HardToKill"):
            amounts = sorted({max(1, round(number * hp / (survival + 1))) for number in DOSES[name]})
        elif name in actions["statusCardValues"]:
            minimum = actions.get("minAmounts", {}).get(name, 1)
            amounts = (minimum, minimum + 1)
        else:
            amounts = DOSES.get(name, (1,))
        if name in actions["statusCardValues"]:
            for pile in ("top", "draw", "discard"):
                for amount in amounts:
                    yield Probe(name, amount, pile)
        elif name in CARDS:
            yield Probe(name, 1, "hand")
        elif name in GENERATED:
            default = GENERATED[name][0]
            weights = settings["buffs"][name].get("generatedCards", {})
            variants = [default]
            variants.extend(card for card, weight in sorted(weights.items(), key=lambda item: -item[1])
                            if card != default and card in CARDS and weight > 0)
            for card in variants[:2]:
                for amount in amounts:
                    yield Probe(name, amount, generated=card)
        else:
            for amount in amounts:
                yield Probe(name, amount)


def unit_card(settings, card, player_action, behind=0):
    actions = settings["actions"]
    ratio = max(actions["statusCardPlayerActionValueRatios"]["Dazed"],
                actions["statusCardPlayerActionValueRatios"].get(card, 0))
    unit = actions["statusCardValues"][card]
    value = max(0, unit - ratio * predict(settings["playerAction"], 0)) + ratio * player_action
    percent = actions.get("laterMonsterPercent", {}).get(card, 0)
    return value * (1 + percent / 100) ** behind


def configured_cost(settings, profile, player_action, rooms, probe, quote, effective):
    actions = settings["actions"]
    name = probe.name
    hp, allies, guard, enemy_count, behind = quote
    if name == "Block":
        effective = min(effective, max(0, player_action / max(1, enemy_count) - guard))
    # The live planner uses the snapshot at intent selection.
    survival = (hp + 0.5 * allies) / player_action
    combat_turns = (hp + allies) / player_action
    factors = {
        "amount": probe.amount, "effectiveamount": effective,
        "actionvalue": profile["actionValue"] * settings["pools"]["actionValueMultiplier"],
        "attackfrequency": profile["attackFrequency"], "survivalturns": survival, "playeractionvalue": player_action,
        "inverseamount": 1 / probe.amount, "hppersurvivalturn": hp / (survival + 1), "roomsentered": rooms + 1,
        "hits": 1, "fixed": 1, "combatturns": combat_turns,
    }
    if name in actions["statusCardValues"]:
        long_fight_share = max(0, min(1, (combat_turns - 1) / 4))
        timing = {"top": 1, "draw": 0.60 + 0.40 * long_fight_share,
                  "discard": 0.20 + 0.80 * long_fight_share}
        return (probe.amount * unit_card(settings, name, player_action, behind)
                * actions["cardPlacementValues"][probe.pile] * timing[probe.pile])
    state = settings["buffs"].get(name)
    formula = state["formula"] if state else actions["valuation"][name]
    price = sum(value * math.prod(factors[term] for term in key.lower().split("*")) for key, value in formula.items())
    if probe.generated and probe.generated != GENERATED[name][0]:
        price *= unit_card(settings, probe.generated, player_action, behind) / unit_card(settings, GENERATED[name][0], player_action, behind)
    percent = (state or {}).get("laterMonsterPercent", actions.get("laterMonsterPercent", {}).get(name, 0))
    return price * (1 + percent / 100) ** behind


def scenes(multi):
    if multi:
        for rooms, name, order, source in MULTI_CASES:
            positions = tuple(range(len(order)))
            yield rooms, name, order, source, positions, "original"
            last = len(order) - 1
            # Reverse enemy actions while keeping the player's target priority on the same enemies.
            yield rooms, name, order[::-1], last - source, positions[::-1], "reversed"
    else:
        for rooms, *ids in (*CASES, (15, "SOUL_FYSH"), (40, "MECHA_KNIGHT")):
            yield rooms, "+".join(ids), ids, 0, tuple(range(len(ids))), "original"


def run(settings, profiles, seeds, only=None, independent=False, multi=False, toxic_priority=4, case=None,
        respect_thorns=True, avoid_temporary_thorns=False):
    roster = {entry["id"]: entry for entry in load(ROOT / "data/monster-action-baselines.json")["monsters"]}
    if multi:
        only = MULTI_PROBES if only is None else only
        for key, loop in MULTI_CYCLES.items():
            roster[key] = {**roster[key], "deterministicCycle": {"openingBeforeLoop": [], "loop": loop}}
    for rooms, encounter, ids, actor, target_order, order in scenes(multi):
        if case is not None and encounter != case:
            continue
        actor_id = ids[actor]
        profile = profiles["monsters"][actor_id]
        player_action = predict(settings["playerAction"], rooms)
        fighters = [reference_fighter(roster[key], profiles["monsters"][key]["hp"]) for key in ids]
        attacks = [index for index in range(12) if fighters[actor].at(index).hits]
        hp = fighters[actor].hp
        ally_hp = sum(fighter.hp for fighter in fighters) - hp
        probes = [probe for probe in recipes(settings, hp, (hp + ally_hp / 2) / player_action)
                  if only is None or probe.name in only]
        for build_index, build in enumerate(ADDITIONS):
            deck = build_deck(build, rooms)
            deck[deck.index(next(card for card in deck if card.block))] = Card(damage=5, vulnerable=2)
            if independent:
                deck[deck.index(BASIC_ATTACK)] = Card(cost=2, damage=14 + rooms // 3)
                deck.extend((Card(block=7 + rooms // 6), Card(cost=0, damage=3, draw=1)))
            anchor = attacks[int(build_index == (2 if independent else 1))]
            starting_seed = 10000 if independent else 0
            seeds_for_group = range(starting_seed, starting_seed + seeds)
            def make_fight(seed, probe=None):
                return Fight(fighters, deck, seed, anchor, probe, actor, target_order, toxic_priority,
                             respect_thorns, avoid_temporary_thorns)

            baselines = [make_fight(seed).run() for seed in seeds_for_group]
            references = [make_fight(seed, Probe("Attack", ATTACK_REFERENCE)).run() for seed in seeds_for_group]
            for probe in probes:
                observations = [[], []]
                for seed, (base, ref) in enumerate(zip(baselines, references)):
                    fight = make_fight(starting_seed + seed, probe)
                    lost, rounds, effective, generated = fight.run()
                    if effective is None or (effective == 0 and probe.name == "Heal"):
                        continue
                    cost = configured_cost(settings, profile, player_action, rooms, probe, fight.quote, effective)
                    observations[seed % 4 == 0].append((lost - base[0], ref[0] - base[0], rounds - base[1],
                                                       cost, generated, fight.quote[4],
                                                       fight.reflection_hits, fight.reflection_hp,
                                                       (fight.quote[0] + fight.quote[1]) / player_action))
                for subset, pairs in zip(("fit", "holdout"), observations):
                    if not pairs:
                        continue
                    delta, reference, rounds_delta, cost, generated, behind, reflected, reflection_hp, combat_turns = map(
                        statistics.mean, zip(*pairs))
                    equivalent = ATTACK_REFERENCE * delta / reference if reference >= 0.6 else None
                    yield {
                        "effect": probe.name, "amount": probe.amount, "pile": probe.pile, "generated_card": probe.generated,
                        "encounter": encounter, "enemy_action_order": "+".join(ids), "order_variant": order,
                        "actor": actor_id, "actor_position": actor + 1, "later_alive_mean": round(behind, 4),
                        "rooms": rooms, "build": build, "round_applied": anchor + 1,
                        "expected_combat_turns_mean": round(combat_turns, 4),
                        "sample": subset, "pairs": len(pairs), "player_hp_delta": round(delta, 4),
                        "attack_reference_hp_delta": round(reference, 4), "round_delta": round(rounds_delta, 4),
                        "reflected_hits_mean": round(reflected, 4), "reflection_hp_loss_mean": round(reflection_hp, 4),
                        "generated_mean": round(generated, 4), "configured_cost": round(cost, 4),
                        "equivalent_attack": None if equivalent is None else round(equivalent, 4),
                        "price_ratio": None if equivalent is None or cost <= 0 else round(equivalent / cost, 4),
                    }
        print(f"{encounter} ({rooms} rooms, {order}, actor {actor_id}) finished", flush=True)


def summarize(rows):
    cohorts = defaultdict(lambda: defaultdict(list))
    for row in rows:
        ratio = row["price_ratio"]
        if ratio is not None and row["pairs"] >= (16 if row["sample"] == "fit" else 6):
            cohort = row["sample"], row["encounter"], row["build"], row["order_variant"]
            cohorts[row["effect"]][cohort].append(ratio)
    result = {}
    for name in sorted({row["effect"] for row in rows}):
        fit, holdout = ([
            statistics.median(ratios) for (label, _, _, _), ratios in cohorts[name].items() if label == sample
        ] for sample in ("fit", "holdout"))
        median = statistics.median(fit) if fit else None
        check = statistics.median(holdout) if holdout else None
        direction = 1 if median is not None and median > 1 else -1
        agreement = sum((value - 1) * direction > 0 for value in fit) / len(fit) if fit else 0
        result[name] = {
            "fit_median_ratio": median, "holdout_median_ratio": check,
            "fit_groups": len(fit), "holdout_groups": len(holdout), "direction_agreement": round(agreement, 3),
            "trial_multiplier": None,
        }
        if (len(fit) >= 9 and len(holdout) >= 7 and abs(median - 1) >= 0.25 and agreement >= 0.74
                and (check - 1) * direction >= 0.15):
            result[name]["trial_multiplier"] = round(max(0.8, min(1.25, 1 + 0.35 * (median - 1))), 3)
    return result


def steam_terminal(settings, seeds):
    rows = []
    current = settings["buffs"][DEATH_PHASE_EFFECT]["formula"]["amount"]
    for rooms in (12, 15, 18):
        for build in ADDITIONS:
            deck = build_deck(build, rooms)
            blocks = []
            for seed in range(seeds):
                rng = random.Random(seed)
                draw, hand = deck[:], []
                rng.shuffle(draw)
                draw_cards(5, draw, [], hand, rng)
                energy = 3
                guard = 0
                while hand:
                    def score(card):
                        return (card.block + 3 * card.draw + 4 * card.energy) / max(1, card.cost) if card.cost <= energy else 0

                    card = max(hand, key=score)
                    if score(card) <= 0:
                        break
                    hand.remove(card)
                    guard += card.block
                    energy += card.energy - card.cost
                    draw_cards(card.draw, draw, [], hand, rng)
                blocks.append(guard)
            for base in (15, 20):
                pressure = lambda extra: statistics.mean(max(0, base + extra - guard) for guard in blocks)
                reference = pressure(ATTACK_REFERENCE) - pressure(0)
                for amount in (3, 6, 9):
                    delta = pressure(amount) - pressure(0)
                    rows.append({
                        "floor": rooms, "build": build, "base_explosion": base, "amount": amount,
                        "configured_cost": amount * current, "player_hp_delta": round(delta, 4),
                        "equivalent_at_explosion": round(ATTACK_REFERENCE * delta / reference, 4) if reference else None,
                    })
    return {"method": "Conditional on Waterfall Giant reaching its explosion. Added stacks and the attack reference change the same death attack.",
            "seeds": seeds, "rows": rows}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, help="Use this full Mod config instead of the live config")
    parser.add_argument("--seeds", type=int, default=64)
    parser.add_argument("--only", nargs="+", help="Measure these effects only")
    parser.add_argument("--independent", action="store_true", help="Use alternate decks, seeds and timing")
    parser.add_argument("--multi", action="store_true", help="Measure static groups with two or three enemies")
    parser.add_argument("--case", help="Measure only this encounter")
    parser.add_argument("--toxic-priority", type=float, default=4, help="Card choice utility for exhausting Toxic")
    parser.add_argument("--ignore-thorns", action="store_true", help="Ignore retaliation when choosing cards and targets")
    parser.add_argument("--avoid-temporary-thorns", action="store_true", help="Avoid attacks that expose HP to temporary retaliation")
    parser.add_argument("--steam-only", action="store_true", help="Measure only the conditional Waterfall Giant explosion")
    parser.add_argument("--output", type=Path, default=ROOT / "dist/attack-calibration")
    args = parser.parse_args()
    settings_file, profiles_file = (args.config, args.config) if args.config else config_source()
    s, p = load(settings_file), load(profiles_file)
    settings, profiles = s.get("Settings", s), p.get("Profiles", p)
    args.output.mkdir(parents=True, exist_ok=True)
    if args.steam_only:
        path = args.output / "steam-eruption.json"
        path.write_text(json.dumps(steam_terminal(settings, args.seeds), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(path)
        return
    rows = list(run(settings, profiles, args.seeds, set(args.only) if args.only else None, args.independent,
                    args.multi, args.toxic_priority, args.case, not args.ignore_thorns, args.avoid_temporary_thorns))
    if not rows:
        parser.error("The requested effects and encounter produced no comparisons.")
    with (args.output / "action-values.csv").open("w", encoding="utf-8-sig", newline="") as handle:
        csv_writer = csv.DictWriter(handle, fieldnames=rows[0])
        csv_writer.writeheader()
        csv_writer.writerows(rows)
    summary = {
        "method": "Extra effect vs baseline vs +6 base damage on the same attack. Configured curve and independent staged example decks.",
        "model": "Single-player synthetic fights using archived base attack and block sequences. Card choices and downgrade effects are approximate.",
        "excluded": sorted(EXCLUDED), "anchor": "Attack", "insufficient_native_model": [DEATH_PHASE_EFFECT],
        "seeds_per_case": args.seeds, "multi": args.multi, "toxic_priority": args.toxic_priority,
        "respect_thorns": not args.ignore_thorns,
        "avoid_temporary_thorns": args.avoid_temporary_thorns,
        "rows": len(rows), "effects": summarize(rows),
    }
    (args.output / "action-values-summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{len(summary['effects'])} effects, {len(rows)} comparison rows")


if __name__ == "__main__":
    main()
