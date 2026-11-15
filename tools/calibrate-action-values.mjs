import { existsSync, readFileSync, writeFileSync } from 'node:fs';

const reference = new URL('../data/monster-action-baselines.json', import.meta.url);
const profilesPath = new URL('../data/monster-profiles.json', import.meta.url);
const auditPath = new URL('../data/action-value-calibration.json', import.meta.url);
const archive = JSON.parse(readFileSync(reference, 'utf8'));
const source = archive.sources.targetMonsterExport;
const referenceArgument = process.argv.indexOf('--reference');
const published = new Map((await readPublishedMonsters()).map(monster => [monster.id, monster]));
const profileText = readFileSync(profilesPath, 'utf8');
const profiles = JSON.parse(profileText);
const previousAudit = existsSync(auditPath) ? JSON.parse(readFileSync(auditPath, 'utf8')).monsters : {};
const excluded = new Set(profiles.keepVanilla);
const difficulties = ['normal', 'ascension'];
const maxGrowthTurns = 5;
const round = number => Math.round(number * 100) / 100;
const mean = values => values.reduce((total, value) => total + value, 0) / values.length;
const isAttack = move => move.kind === 'direct_attack';
const isGrowth = effect => effect.target === 'self' && ['STRENGTH', 'RITUAL'].includes(effect.id);

const encounterSamples = {
    SEWER_CLAM: { turns: 4.97, source: 'https://spire-codex.com/monsters/sewer_clam#encounters' }
};

async function readPublishedMonsters() {
    if (referenceArgument >= 0)
        return JSON.parse(readFileSync(process.argv[referenceArgument + 1], 'utf8'));

    const response = await fetch(source).catch(error => {
        throw new Error('Download the linked Spire Codex export and rerun with --reference <file>.', { cause: error });
    });
    if (!response.ok) throw new Error(`Spire Codex: HTTP ${response.status}`);
    return response.json();
}

function publishedDamage(move, referenceMove) {
    const damage = referenceMove?.damage;
    if (!isAttack(move) || !Number.isFinite(move.directAttackValue.normal) || !Number.isFinite(damage?.normal))
        return move;

    const hits = damage.hit_count ?? move.damage.hits;
    const ascensionHits = damage.hit_count_ascension ?? damage.hit_count ?? move.damage.ascensionHits;
    return {
        ...move,
        damage: { hits, ascensionHits },
        directAttackValue: { normal: damage.normal * hits, ascension: (damage.ascension ?? damage.normal) * ascensionHits }
    };
}

function knownCost(move, difficulty, strength = 0, estimateGrowth = false) {
    const hits = difficulty === 'ascension' ? move.damage.ascensionHits : move.damage.hits;
    const extras = move.nonAttackEffects.filter(effect =>
        Array.isArray(effect.value) && (estimateGrowth || !isGrowth(effect)));
    return move.directAttackValue[difficulty] + strength * hits +
        extras.reduce((total, effect) => total + mean(effect.value), 0);
}

function growthWindow(opening, loop, difficulty, turns) {
    let strength = 0;
    let ritual = 0;
    let total = 0;
    let attackWeight = 0;
    const attackSequence = [];
    for (let i = 0; i < turns; i++) {
        const move = opening[i] ?? loop[(i - opening.length) % loop.length];
        const weight = Math.min(turns - i, 1);
        if (isAttack(move)) {
            const value = knownCost(move, difficulty, strength);
            attackSequence.push([i + 1, round(value), weight]);
            total += value * weight;
            attackWeight += weight;
        }

        // Enemy Ritual skips the turn on which it is first gained.
        let nextRitual = 0;
        for (const effect of move.nonAttackEffects.filter(isGrowth)) {
            const amount = difficulty === 'ascension' ? effect.ascensionAmount ?? effect.amount : effect.amount;
            if (effect.id === 'STRENGTH') strength += amount;
            else nextRitual += amount;
        }
        strength += ritual;
        ritual += nextRitual;
    }
    return { value: attackWeight > 0 ? total / attackWeight : null, attackSequence };
}

function calibrate(monster, profile) {
    if (excluded.has(monster.id) || profile.enabled === false)
        return { method: 'vanilla' };

    const referenceMoves = new Map(published.get(monster.id).moves.map(move => [move.id, move]));
    const calibratedMoves = monster.moves.map(move => publishedDamage(move, referenceMoves.get(move.id)));
    const attacks = calibratedMoves.filter(isAttack);
    if (attacks.length === 0)
        return { method: 'noKnownAttack' };
    if (attacks.some(move => difficulties.some(difficulty => !Number.isFinite(move.directAttackValue[difficulty]))))
        return { method: 'unknownDamage' };

    if (monster.id === 'TUNNELER' && profile.randomizeWhenPower === 'BURROWED') {
        const move = attacks.find(move => move.id === 'FROM_BELOW');
        return { method: 'burrowed', moves: [move.id], values: difficulties.map(difficulty => knownCost(move, difficulty)) };
    }

    const cycle = monster.deterministicCycle;
    if (!cycle) {
        return {
            method: attacks.length === 1 ? 'onlyKnownAttack' : 'unweightedAttacks',
            moves: attacks.map(move => move.id),
            values: difficulties.map(difficulty =>
                mean(attacks.map(move => knownCost(move, difficulty, 0, true))))
        };
    }

    const moves = new Map(calibratedMoves.map(move => [move.id, move]));
    const opening = cycle.openingBeforeLoop.map(id => moves.get(id));
    const loop = cycle.loop.map(id => moves.get(id));
    if ([...opening, ...loop].some(move => !move))
        throw new Error(`${monster.id}: unknown move in cycle`);
    const loopAttacks = loop.filter(isAttack);
    const usedAttacks = loopAttacks.length > 0 ? loopAttacks : opening.filter(isAttack);
    const cycleValues = difficulties.map(difficulty => mean(usedAttacks.map(move => knownCost(move, difficulty))));
    const growth = [...opening, ...loop].flatMap(move => move.nonAttackEffects.filter(isGrowth));
    const route = { opening: cycle.openingBeforeLoop, loop: cycle.loop };
    if (growth.length === 0 || growth.some(effect => !Number.isFinite(effect.amount)))
        return { method: 'cycle', ...route, values: cycleValues };

    const sample = encounterSamples[monster.id];
    const turns = Math.min(sample ? Math.round(sample.turns) : profile.survivalTurns ?? profiles.defaults.survivalTurns, maxGrowthTurns);
    const windows = difficulties.map(difficulty => growthWindow(opening, loop, difficulty, turns));
    return {
        method: 'growthCycle', ...route, turns, ...(sample ? { encounterSample: sample } : {}),
        attacks: windows.map(window => window.attackSequence),
        values: windows.map((window, i) => Math.max(cycleValues[i], window.value ?? 0))
    };
}

const audit = {};
const calibrated = new Map();
const coverage = {};
for (const monster of archive.monsters) {
    const profile = profiles.monsters[monster.id];
    const original = previousAudit[monster.id]?.previous ?? [profile.actionValue, profile.deadlyEnemies ?? null];
    const { values, ...evidence } = calibrate(monster, profile);
    const next = values?.map(round) ?? original;
    if (values && next[0] === next[1] && original[1] === null)
        next[1] = null;
    if (!Number.isFinite(next[0]) || next[0] <= 0 || (next[1] !== null && (!Number.isFinite(next[1]) || next[1] <= 0)))
        throw new Error(`${monster.id}: invalid calibrated action value`);
    audit[monster.id] = { previous: original, proposed: next, ...evidence };
    calibrated.set(monster.id, next);
    coverage[evidence.method] = (coverage[evidence.method] ?? 0) + 1;
}

const metadata = {
    targetVersion: archive.targetVersion,
    source,
    publishedMonsterCount: published.size,
    scale: '一次攻击招的等效伤害。数值对为 [常规, 致命敌人]；null 表示高进阶沿用常规值。',
    method: '逐段基础伤害加该攻击附带的可估效果。已知循环按攻击招的实际出现次数取均值，成长按后续攻击结算。未知循环按已知攻击招等权估计，并沿用附带成长的归档估值中点。原生开场状态和独立非攻击回合的估值不直接折入。纯辅助或伤害未知的画像维持原值。',
    growth: `固定循环按预期寿命的前 ${maxGrowthTurns} 回合以内计算，末回合按小数部分加权。附有 encounterSample 的条目采用社区该场战斗平均回合数的整数近似。力量计入之后的每段攻击，仪式跳过施加当回合的增长。取这段攻击的均值与无成长循环均值的较大值。attacks 依进阶列出 [回合, 分值, 权重]。`,
    coverage
};

const lines = Object.entries(audit).map(([id, row]) => `    ${JSON.stringify(id)}: ${JSON.stringify(row)}`);
writeFileSync(auditPath, JSON.stringify(metadata, null, 2).replace(/\n}$/, ',\n') +
    `  "monsters": {\n${lines.join(',\n')}\n  }\n}\n`);
if (process.argv.includes('--apply')) {
    const rowPattern = /^( +"([A-Z0-9_]+)": \{ "name": .*?"actionValue": )([\d.]+)(?:, "deadlyEnemies": [\d.]+)?(?=,)/gm;
    const nextText = profileText.replace(rowPattern, (line, prefix, id) => {
        const values = calibrated.get(id);
        calibrated.delete(id);
        return values ? prefix + values[0] + (values[1] === null ? '' : `, "deadlyEnemies": ${values[1]}`) : line;
    });
    if (calibrated.size > 0)
        throw new Error(`Could not update profiles: ${[...calibrated.keys()].join(', ')}`);
    writeFileSync(profilesPath, nextText);
}
