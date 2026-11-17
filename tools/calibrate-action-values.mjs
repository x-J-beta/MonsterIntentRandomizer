import { existsSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { parseArgs } from 'node:util';

const { values: args } = parseArgs({
    options: { apply: { type: 'boolean' }, 'sync-game': { type: 'string' } }
});
const data = name => new URL(`../data/${name}`, import.meta.url);
const read = path => JSON.parse(readFileSync(path, 'utf8').replace(/^\uFEFF/, ''));
const archive = read(data('monster-action-baselines.json'));
const { moves: nativeMoves, sequences } = read(data('monster-moves.json'));
const { playerAction } = read(data('settings.json'));
const profilePath = data('monster-profiles.json');
const profileText = readFileSync(profilePath, 'utf8');
const { monsters: profiles } = JSON.parse(profileText);
const auditPath = data('action-value-calibration.json');
const oldAudit = existsSync(auditPath) ? read(auditPath).monsters : {};
const discount = 1.03;
const referenceRooms = { act1: 5, act2: 18, act3: 33 };
const referenceTurns = { SEWER_CLAM: 4.97 };
const round = value => Math.round(value * 100) / 100;
const archiveId = id => id.replace(/_MOVE$/, '');
const isAttack = move => move?.kind === 'direct_attack';
const hasDamage = move => isAttack(move) && Number.isFinite(move.directAttackValue.normal);
const isGrowth = effect => effect.target === 'self' && ['STRENGTH', 'RITUAL'].includes(effect.id);
const effectValue = (move, includeGrowth = false) => move.nonAttackEffects.reduce((total, effect) =>
    Array.isArray(effect.value) && (includeGrowth || !isGrowth(effect))
        ? total + (effect.value[0] + effect.value[1]) / 2 : total, 0);

function routeFor(monster, phaseId, phase) {
    const moves = new Map(monster.moves.map(move => [move.id, move]));
    const machine = monster.stateMachine;
    const states = new Map();
    for (const state of machine?.states ?? []) {
        if (!states.has(state.id) || state.branches?.length)
            states.set(state.id, state);
    }
    const moveFor = id => moves.get(states.get(id)?.move_id ?? id) ?? moves.get(archiveId(id));
    const presets = sequences[monster.id];
    const preset = phaseId ? presets?.phases?.[phaseId] : presets;
    const cycle = monster.deterministicCycle;
    if (preset || (!phaseId && cycle)) {
        const opening = preset?.opening ?? cycle?.openingBeforeLoop ?? [];
        const loop = preset?.loop ?? cycle.loop;
        const prefix = Array.from({ length: preset?.repeatOpening ?? 1 }, () => opening).flat();
        return {
            method: 'cycle', moveFor, opening, loop,
            next: (_, i) => [[prefix[i] ?? loop[(i - prefix.length) % loop.length], 1]]
        };
    }

    const initial = phase?.moves[0] ?? machine?.initial_move;
    const resolveState = id => states.get(id) ?? [...states.values()].find(state => state.move_id === id);
    const next = (path, i) => {
        if (i === 0)
            return initial && moveFor(initial) ? [[initial, 1]] : [];
        const successor = resolveState(path.id)?.next;
        if (moveFor(successor ?? ''))
            return [[successor, 1]];
        const node = states.get(successor);
        if (node?.type !== 'random' || !node.branches?.length ||
            node.branches.some(branch => !Number.isFinite(branch.weight) || branch.condition || !moveFor(branch.move_id)))
            return [];
        const branches = node.branches.filter(branch => {
            const id = moveFor(branch.move_id).id;
            if (branch.repeat === 'UseOnlyOnce')
                return !path.history.includes(id);
            const maxRepeat = branch.repeat === 'CannotRepeat' ? 1 :
                branch.repeat === 'CanRepeatXTimes' ? branch.max_times : Infinity;
            return branch.weight > 0 && (maxRepeat === Infinity ||
                path.history.slice(-maxRepeat).filter(previous => previous === id).length < maxRepeat);
        });
        const total = branches.reduce((sum, branch) => sum + branch.weight, 0);
        return branches.map(branch => [branch.move_id, branch.weight / total]);
    };
    return { method: initial && moveFor(initial) ? 'stateMachine' : 'unordered', moveFor, next };
}

function calibrate(monster, profile, phaseId, phase) {
    if (profile.enabled === false || phase?.enabled === false)
        return { method: 'disabled' };

    const keep = new Set(profile.keepMoves ?? []);
    const known = nativeMoves[monster.id] ?? [];
    const route = routeFor(monster, phaseId, phase);
    const { moveFor } = route;
    const keptMoveIds = new Set([...keep].map(id => moveFor(id)?.id));
    const eligibleMoveIds = new Set((phase?.moves ?? []).map(id => moveFor(id)?.id));
    const unnamedProfileMoves = new Set([...known, ...(phase?.moves ?? [])]
        .filter(id => !keep.has(id) && moveFor(id)).map(id => moveFor(id).id));
    const canScore = (id, move) => move && !keep.has(id) &&
        (!keptMoveIds.has(move.id) || unnamedProfileMoves.has(move.id)) &&
        (!phase || eligibleMoveIds.has(move.id));

    if (known.length > 0 && known.every(id => keep.has(id)))
        return { method: 'vanilla' };

    const eligible = monster.moves.filter(move => canScore(move.id, move));
    const attacks = eligible.filter(hasDamage);
    const unknownAttacks = eligible.filter(move => isAttack(move) && !hasDamage(move)).map(move => move.id);
    if (!attacks.length && unknownAttacks.length)
        return { method: 'unknownDamage', unknownAttacks };

    const support = attacks.length === 0;
    const candidates = support
        ? eligible.filter(move => move.kind === 'non_attack' && effectValue(move, true) > 0) : attacks;
    if (!candidates.length)
        return { method: 'insufficientData' };

    const stage = phase?.stage ?? profile.stage ??
        ({ 'Act 2 - Hive': 'act2', 'Act 3 - Glory': 'act3' }[monster.encounters?.[0]?.act]) ?? 'act1';
    const rooms = referenceRooms[stage] ?? referenceRooms.act1;
    const player = (playerAction.base + rooms / playerAction.roomsPerStep) ** playerAction.exponent;
    const firstCycle = (route.opening?.length ?? 0) + (route.loop?.length ?? 0);
    const estimatedTurns = referenceTurns[monster.id] ?? ((phase?.hp ?? profile.hp) / player);
    const turns = Math.min(5, Math.max(3, firstCycle, estimatedTurns));
    const candidateIds = new Set(candidates.map(move => move.id));
    const traces = [];
    let paths = [{ probability: 1, strength: 0, ritual: 0, history: [] }];
    let fallback = false;
    for (let i = 0; i < Math.ceil(turns); i++) {
        const timeWeight = Math.min(1, turns - i);
        const step = { weight: 0, value: 0 };
        const nextPaths = [];
        const score = (move, path, probability) => {
            const strength = hasDamage(move) ? path.strength * move.damage.hits : 0;
            const value = (move.directAttackValue.normal ?? 0) + strength + effectValue(move, support);
            step.weight += probability * timeWeight;
            step.value += value * probability * timeWeight;
        };
        for (const path of paths) {
            const options = route.next(path, i);
            if (!options.length) {
                // Score the unresolved tail once, at the first turn without verified ordering.
                fallback = true;
                for (const move of candidates)
                    score(move, path, path.probability / candidates.length);
                continue;
            }
            for (const [id, share] of options) {
                const move = moveFor(id);
                if (!move)
                    throw new Error(`${monster.id}: no archived move for ${id}`);
                const probability = path.probability * share;
                const eligible = canScore(id, move);
                if (eligible && candidateIds.has(move.id))
                    score(move, path, probability);

                const growth = eligible ? move.nonAttackEffects.filter(isGrowth) : [];
                let strength = path.strength + path.ritual;
                let ritual = path.ritual;
                for (const effect of growth) {
                    if (effect.id === 'STRENGTH') strength += effect.amount ?? 0;
                    else ritual += effect.amount ?? 0;
                }
                nextPaths.push({ id, probability, strength, ritual, history: [...path.history, move.id] });
            }
        }
        if (step.weight > 0)
            traces.push({ turn: i + 1, ...step });
        paths = nextPaths;
        if (!paths.length)
            break;
    }

    if (!traces.length)
        return { method: 'noScoredTurn', turns: round(turns), moves: candidates.map(move => move.id) };
    const weight = traces.reduce((sum, step) => sum + step.weight, 0);
    const baseline = traces.reduce((sum, step) => sum + step.value, 0) / weight;
    const actionValue = traces.reduce((sum, step) => sum + step.value / discount ** (step.turn - 1), 0) / weight;
    return {
        method: route.method, actionValue: round(actionValue), withoutDiscount: round(baseline), turns: round(turns),
        moves: candidates.map(move => move.id),
        ...(route.opening ? { opening: route.opening, loop: route.loop } : {}),
        ...(support ? { support: true } : {}),
        ...(fallback ? { unorderedTail: true } : {}),
        ...(unknownAttacks.length ? { unknownAttacks } : {}),
        turnsScored: traces.map(step => [step.turn, round(step.weight), round(step.value / step.weight)])
    };
}

function auditRow(result, value, before) {
    return { before: before ?? value, proposed: result.actionValue ?? value, ...result };
}

const archived = new Map(archive.monsters.map(monster => [monster.id, monster]));
const audit = {};
for (const [id, profile] of Object.entries(profiles)) {
    const monster = archived.get(id);
    const phases = Object.entries(profile.phases ?? {});
    const previous = oldAudit[id];
    const result = !monster ? { method: 'noArchive' } : phases.length ? { method: 'phased' } : calibrate(monster, profile);
    const row = auditRow(result, profile.actionValue, previous?.before);
    if (phases.length && monster) {
        row.phases = Object.fromEntries(phases.map(([key, phase]) => [
            key, auditRow(calibrate(monster, profile, key, phase), phase.actionValue ?? profile.actionValue,
                previous?.phases?.[key]?.before)
        ]));
    }
    audit[id] = row;
}

const entries = Object.values(audit).flatMap(row => [row, ...Object.values(row.phases ?? {})]);
const calibrated = entries.filter(row => row.actionValue !== undefined);
const metadata = {
    targetVersion: archive.targetVersion, lateActionDivisor: discount, referenceRooms, referenceTurns,
    scale: '每次攻击招的等效伤害。配置采用普通难度，运行时使用统一高进阶倍率。',
    method: '只对可随机的招式评分。已知攻击伤害加可估的附带效果，纯辅助画像采用已知辅助效果。固定招式占用原版时间。力量和仪式在后续攻击兑现。窗口按画像血量和该幕玩家行动力估算，至少 3 回合并覆盖首轮已知循环，最多 5 回合。末回合按小数部分计权。没有可靠顺序的后续招式等权作为一个样本。总分逐回合除以 1.03^(回合-1)，再除以未折算的招式权重总和。',
    turnsScored: '[回合, 样本权重, 折算前分值]',
    calibrated: calibrated.length,
    retained: entries.length - calibrated.length
};
const header = JSON.stringify(metadata, null, 2).replace(/\n}$/, ',\n');
const rows = Object.entries(audit).map(([id, row]) => `    ${JSON.stringify(id)}: ${JSON.stringify(row)}`);
writeFileSync(auditPath, `${header}  "monsters": {\n${rows.join(',\n')}\n  }\n}\n`);

if (args.apply) {
    let monsterId;
    const text = profileText.replace(/^( {4}| {8})("([^"\n]+)": \{[^\n]*?"actionValue":\s*)([\d.]+)/gm,
        (line, indent, prefix, id) => {
            if (indent.length === 4)
                monsterId = id;
            const row = indent.length === 4 ? audit[id] : audit[monsterId]?.phases?.[id];
            return row?.actionValue === undefined ? line : indent + prefix + row.proposed;
        });
    if (text !== profileText)
        writeFileSync(profilePath, text);
}

if (args['sync-game']) {
    const path = args['sync-game'];
    const config = read(path);
    if (!config.Profiles?.monsters)
        throw new Error('The game config has no monster profiles.');

    let updated = 0;
    const custom = [];
    function sync(profile, row, key) {
        if (!profile || row.actionValue === undefined || profile.actionValue === row.proposed)
            return;
        if (profile.actionValue !== row.before) {
            custom.push(key);
            return;
        }
        profile.actionValue = row.proposed;
        updated++;
    }
    for (const [id, row] of Object.entries(audit)) {
        const saved = config.Profiles.monsters[id];
        sync(saved, row, id);
        for (const [phase, result] of Object.entries(row.phases ?? {}))
            sync(saved?.phases?.[phase], result, `${id}/${phase}`);
    }
    if (updated > 0) {
        writeFileSync(`${path}.action-value.tmp`, JSON.stringify(config, null, 2) + '\n');
        renameSync(`${path}.action-value.tmp`, path);
    }
    console.log(`Saved configuration: updated ${updated}; preserved custom action values: ${custom.join(', ') || 'none'}`);
}
console.log(`Calibrated ${calibrated.length}; retained ${metadata.retained}; divisor ${discount}`);

