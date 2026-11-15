import { copyFileSync, existsSync, readFileSync, renameSync, writeFileSync } from 'node:fs';

const read = url => JSON.parse(readFileSync(url, 'utf8'));
const data = name => new URL(`../data/${name}`, import.meta.url);
const profilesPath = data('monster-profiles.json');
const auditPath = data('monster-preference-values.json');
const text = readFileSync(profilesPath, 'utf8');
const profiles = JSON.parse(text);
const settings = read(data('settings.json'));
const archive = read(data('monster-action-baselines.json'));
const existingAudit = existsSync(auditPath) ? read(auditPath).monsters : {};
const styles = ['attack', 'buff', 'defend', 'playerDebuff'];
const references = { act1: 5, act2: 18, act3: 33 };
const excluded = new Set(profiles.keepVanilla);
const defensivePowers = new Set([
    'BARRICADE', 'CURL_UP', 'DEXTERITY', 'FLUTTER', 'HARDENED_SHELL', 'HARD_TO_KILL',
    'INTANGIBLE', 'MALLEABLE', 'PLATING', 'SLIPPERY'
]);
const effectAliases = {
    CHAINS_OF_BINDING: 'Soulbind', MIND_ROT: 'DrawDown', WASTE_AWAY: 'EnergyDown',
    REGENERATION: 'Regen'
};
const normalize = value => value.replaceAll('_', '').toLowerCase();
const valuation = new Map(Object.entries(settings.actions.valuation).map(([id, formula]) => [normalize(id), formula]));
const buffValuation = new Map(Object.entries(settings.buffs).map(([id, buff]) => [normalize(id), buff.formula]));
const round = value => Math.round(value * 100) / 100;
const midpoint = range => Array.isArray(range) && range.every(Number.isFinite)
    ? (range[0] + range[1]) / 2 : null;

function moveSubset(monster, profile) {
    const states = monster.stateMachine?.states ?? [];
    const allows = profile.randomMoves ? new Set(profile.randomMoves) : null;
    const kept = new Set(profile.keepMoves ?? []);
    const eligible = monster.moves.filter(move => {
        const statesForMove = states.filter(state => state.move_id === move.id);
        if (allows)
            return statesForMove.some(state => allows.has(state.id) && !kept.has(state.id));
        return !kept.has(move.id) && (!statesForMove.length || statesForMove.some(state => !kept.has(state.id))) &&
            ['direct_attack', 'non_attack'].includes(move.kind) &&
            !move.nonAttackEffects.some(effect => ['summon', 'escape', 'special'].includes(effect.kind));
    });
    const seen = new Set();
    return eligible.filter(move => {
        const signature = JSON.stringify([move.id.replace(/(?:_MOVE)?_\d+$/, ''), move.directAttackValue, move.nonAttackEffects]);
        if (seen.has(signature))
            return false;
        seen.add(signature);
        return true;
    });
}

function moveValues(move, profile, factors) {
    const result = Object.fromEntries(styles.map(style => [style, 0]));
    const missing = new Set();
    const hasSteamEruption = move.nonAttackEffects.some(effect => effect.id === 'STEAM_ERUPTION');
    if (move.kind === 'direct_attack') {
        if (!Number.isFinite(move.directAttackValue.normal))
            missing.add('attack');
        result.attack += Number.isFinite(move.directAttackValue.normal) ? move.directAttackValue.normal : profile.actionValue;
    }

    for (const effect of move.nonAttackEffects) {
        const id = effect.id ?? '';
        if (id === 'STEAM_ERUPTION')
            continue;
        const player = String(effect.target ?? '').toLowerCase().includes('player');
        const style = effect.kind === 'block' || effect.kind.startsWith('heal') || defensivePowers.has(id) && !player
            ? 'defend' : player || effect.kind.includes('debuff') || effect.kind.includes('status_card')
                ? 'playerDebuff' : effect.kind === 'power' || effect.kind === 'unspecified_buff' ? 'buff' : null;
        if (!style)
            continue;

        const action = player && ['STRENGTH', 'DEXTERITY'].includes(id) && effect.amount < 0
            ? (id === 'STRENGTH' ? 'StrengthDown' : 'DexterityDown')
            : effectAliases[id] ?? (effect.kind === 'block' ? 'Block' : effect.kind === 'heal' ? 'Heal' : id);
        const buff = player ? undefined : buffValuation.get(normalize(action));
        const formula = buff ?? valuation.get(normalize(action));
        const amount = Number.isFinite(effect.amount) ? Math.abs(effect.amount) : 0;
        const inputs = { ...factors, amount, effectiveamount: buff ? amount : Math.min(amount, factors.playeractionvalue),
            inverseamount: amount > 0 ? 1 / amount : 0 };
        const estimated = formula ? Object.entries(formula).reduce((sum, [term, coefficient]) =>
            sum + (term === 'fixed' ? coefficient :
                term.toLowerCase().split('*').reduce((product, factor) => product * (inputs[factor.trim()] ?? 0), coefficient)), 0) : 0;
        const value = estimated > 0 ? estimated : midpoint(effect.value);
        if (value !== null && value > 0)
            result[style] += value;
        else if (effect.kind === 'status_card_unspecified')
            result[style] += settings.actions.statusCardValues.Wound;
        else
            missing.add(style);
    }

    const intentStyles = {
        Buff: 'buff', Defend: 'defend', Heal: 'defend', Debuff: 'playerDebuff',
        DebuffStrong: 'playerDebuff', Status: 'playerDebuff', CardDebuff: 'playerDebuff'
    };
    for (const intent of move.intents)
        if (intentStyles[intent] && result[intentStyles[intent]] === 0 && !(intent === 'Buff' && hasSteamEruption))
            missing.add(intentStyles[intent]);
    for (const style of missing)
        if (result[style] === 0)
            result[style] = profile.actionValue * (profile.nonAttackMultiplier ?? profiles.defaults.nonAttackMultiplier);
    return { result, estimated: [...missing] };
}

const audit = {};
for (const monster of archive.monsters) {
    const profile = profiles.monsters[monster.id];
    if (!profile || excluded.has(monster.id) || profile.enabled === false)
        continue;
    const moves = moveSubset(monster, profile);
    const stage = profile.stage ?? ({ 'Act 2 - Hive': 'act2', 'Act 3 - Glory': 'act3' }[monster.encounters?.[0]?.act]) ?? 'act1';
    const rooms = references[stage] ?? references.act1;
    const player = Math.pow(settings.playerAction.base + rooms / settings.playerAction.roomsPerStep, settings.playerAction.exponent);
    const factors = {
        actionvalue: profile.actionValue, attackfrequency: profile.attackFrequency, survivalturns: profile.hp / player,
        playeractionvalue: player, hppersurvivalturn: player, roomsentered: rooms + 1, hits: 1
    };
    const costs = Object.fromEntries(styles.map(style => [style, 0]));
    const approximations = [];
    for (const move of moves) {
        const { result, estimated } = moveValues(move, profile, factors);
        for (const style of styles)
            costs[style] += result[style];
        if (estimated.length > 0)
            approximations.push([move.id, estimated]);
    }
    const total = Object.values(costs).reduce((a, b) => a + b, 0);
    if (total <= 0)
        continue;

    const percentages = Object.fromEntries(styles.map(style => [style, round(100 * costs[style] / total)]));
    const largest = styles.reduce((best, style) => costs[style] > costs[best] ? style : best, styles[0]);
    percentages[largest] = round(percentages[largest] + 100 - Object.values(percentages).reduce((a, b) => a + b, 0));
    audit[monster.id] = {
        previous: existingAudit[monster.id]?.previous ?? profile.preferences,
        proposed: percentages,
        values: Object.fromEntries(styles.map(style => [style, round(costs[style])])),
        moves: moves.map(move => move.id),
        ...(approximations.length > 0 ? { approximations } : {})
    };
}

const header = {
    targetVersion: archive.targetVersion,
    method: '每个可随机的不同原版招式计一次值，内容相同的编号重复招式合并。攻击按总基础伤害，附带效果优先使用当前配置估值，再用归档估值中点。未知类别按画像的非攻击回合基准估算，未明塞牌按一张伤口的价格估算。四类价值归一到 100，攻击频次单独保留。',
    referenceRooms: references,
    recalculated: Object.keys(audit).length
};
const lines = Object.entries(audit).map(([id, row]) => `    ${JSON.stringify(id)}: ${JSON.stringify(row)}`);
writeFileSync(auditPath, JSON.stringify(header, null, 2).replace(/\n}$/, ',\n') +
    `  "monsters": {\n${lines.join(',\n')}\n  }\n}\n`);
if (process.argv.includes('--apply')) {
    const updated = text.replace(/^(\s*"([A-Z0-9_]+)": \{[^\n]+?"preferences": )\{[^}]+\}/gm,
        (line, prefix, id) => audit[id] ? prefix + JSON.stringify(audit[id].proposed) : line);
    writeFileSync(profilesPath, updated);
}

const syncArgument = process.argv.indexOf('--sync-game');
if (syncArgument >= 0) {
    const path = process.argv[syncArgument + 1];
    if (!path)
        throw new Error('Pass the game config path after --sync-game.');
    const config = read(path);
    const oldMixed = read(data('mixed-monster-actions.json')).monsters;
    const saved = config.Profiles?.monsters;
    if (!saved)
        throw new Error('The game config has no monster profiles.');
    const matches = (left, right) => left && right && styles.every(style =>
        Math.abs((left[style] ?? 0) - (right[style] ?? 0)) < 1e-9);
    let updated = 0;
    for (const [id, row] of Object.entries(audit)) {
        const profile = saved[id];
        if (!profile || matches(profile.preferences, row.proposed))
            continue;
        if (matches(profile.preferences, row.previous) || matches(profile.preferences, oldMixed[id]?.previous.preferences)) {
            profile.preferences = row.proposed;
            updated++;
        }
    }
    if (updated > 0) {
        copyFileSync(path, `${path}.preferences.bak`);
        const temporary = `${path}.preferences.tmp`;
        writeFileSync(temporary, JSON.stringify(config, null, 2) + '\n');
        renameSync(temporary, path);
    }
    console.log(`Updated ${updated} matching default preferences. Custom preferences were retained.`);
}
