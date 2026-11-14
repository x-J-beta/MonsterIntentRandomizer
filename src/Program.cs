using System.Globalization;
using System.Text.Json;
using MonsterIntentRandomizer;

if (args.Length > 0 && args[0] == "--check")
{
    PrototypeChecks.Run();
    return;
}

string folder = args.Length > 3 ? args[3] : Path.Combine(Directory.GetCurrentDirectory(), "data");
var inputs = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
inputs.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "settings.json")));
var root = configuration.RootElement;
var sample = root.GetProperty("simulation");
int seed = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : sample.GetProperty("defaultSeed").GetInt32();
string monsterId = (args.Length > 1 ? args[1] : sample.GetProperty("defaultMonsterId").GetString()!).ToUpperInvariant();
string ascension = args.Length > 2 ? args[2] : sample.GetProperty("defaultActionValueTier").GetString()!;
using var profiles = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "monster-profiles.json")));
var settings = root.GetProperty("pools").Deserialize<PoolSettings>(inputs)!;
var defaults = profiles.RootElement.GetProperty("defaults");
var entry = profiles.RootElement.GetProperty("monsters").GetProperty(monsterId);
var hitWeights = ProfileSetting("hitWeights").Deserialize<Dictionary<int, double>>(inputs)!;
double basicActionValue = entry.GetProperty("actionValue").GetDouble();
if (ascension is not ("standard" or "deadlyEnemies"))
    throw new ArgumentException($"Unknown action value tier: {ascension}.");
var profile = new MonsterProfile(
    ascension == "deadlyEnemies" && entry.TryGetProperty("deadlyEnemies", out var higher) ? higher.GetDouble() : basicActionValue,
    ProfileSetting("volatility").GetDouble(),
    entry.GetProperty("attackFrequency").GetDouble(),
    ProfileSetting("nonAttackMultiplier").GetDouble(),
    ProfileSetting("survivalTurns").GetDouble(),
    entry.GetProperty("preferences").EnumerateObject().ToDictionary(
        field => Enum.Parse<IntentStyle>(field.Name, ignoreCase: true), field => field.Value.GetDouble()),
    hitWeights,
    entry.TryGetProperty("effects", out var effects) ? effects.EnumerateObject().ToDictionary(
        field => Enum.Parse<EffectKind>(field.Name, ignoreCase: true), field => field.Value.GetDouble()) : null,
    entry.TryGetProperty("cards", out var cards) ? cards.Deserialize<StatusPlacement[]>(inputs) : null);
bool enabled = !entry.TryGetProperty("enabled", out var flag) || flag.GetBoolean();
if (!enabled)
{
    Console.WriteLine($"{monsterId}: original actions retained (enabled is false).");
    return;
}

var valuation = root.GetProperty("opening");
var actions = root.GetProperty("actions");
double weakReduction = actions.GetProperty("weakDamageReduction").GetDouble();
double vulnerableIncrease = actions.GetProperty("vulnerableDamageIncrease").GetDouble();
double healCoefficient = actions.GetProperty("heal").GetDouble();
double blockCoefficient = actions.GetProperty("block").GetDouble();
var forecast = root.GetProperty("forecast");
var scenarios = forecast.GetProperty("scenarios").EnumerateArray().Select(scenario => new TargetingScenario(
    scenario.GetProperty("weight").GetDouble(), scenario.GetProperty("singleTargetFocusShare").GetDouble(),
    scenario.GetProperty("areaDamageMultiplier").GetDouble(),
    scenario.TryGetProperty("damageMultiplier", out var roll) ? roll.GetDouble() : 1)).ToArray();
int horizon = forecast.GetProperty("maxEnemyWindows").GetInt32();
double discount = forecast.GetProperty("discount").GetDouble();
double hp = entry.GetProperty("hp").GetDouble();
var allyInputs = sample.GetProperty("allies").EnumerateArray().ToArray();
var enemies = new[]
{
    new EnemySnapshot(monsterId, hp, hp, sample.GetProperty("targetBlock").GetDouble(),
        sample.GetProperty("targetPriority").GetDouble(),
        FirstPlayerWindow: sample.GetProperty("targetFirstPlayerWindow").GetInt32(),
        FirstEnemyAction: sample.GetProperty("targetFirstEnemyAction").GetInt32(),
        FixedOpening: entry.TryGetProperty("fixedOpening", out var opening) && opening.GetBoolean())
}.Concat(allyInputs.Select(ally => ally.Deserialize<EnemySnapshot>(inputs)!)).ToArray();
// A live encounter adapter can supply recent observations on top of the stage prior.
string stage = args.Length > 4 ? args[4] : entry.TryGetProperty("stage", out var profileStage)
    ? profileStage.GetString()! : sample.GetProperty("defaultStage").GetString()!;
var damage = SurvivalForecast.BlendDamage(forecast.GetProperty("playerDamageByStage").GetProperty(stage)
    .Deserialize<DamageEstimate>(inputs)!,
    sample.GetProperty("recentPlayerDamage").Deserialize<DamageEstimate[]>(inputs)!
        .TakeLast(forecast.GetProperty("recentPlayerRoundLimit").GetInt32()),
    forecast.GetProperty("stagePriorWeightInPlayerRounds").GetDouble());
double firstPlayerWindow = sample.GetProperty("firstPlayerWindowFraction").GetDouble();
var baseline = SurvivalForecast.Predict(enemies, monsterId, damage, scenarios, horizon, firstPlayerWindow);
double expectedHits = profile.ExpectedHitsPerAttack;
double attackShare = profile.ExpectedAttackTurnShare;
double expectedAction = profile.ActionValue *
    (attackShare + (1 - attackShare) * profile.NonAttackMultiplier);
double expectedHitDamage = Math.Max(1, Math.Round(profile.ActionValue / Math.Max(1, expectedHits)));
var moves = new[] { new ExpectedMove(expectedAction, attackShare, expectedHitDamage, expectedHits) };
Console.WriteLine($"seed {seed}, {monsterId}, {ascension}, {stage}; target {profile.AttackFrequency:F2} attack hits/turn");
Console.WriteLine($"player damage prior after blending: {damage.SingleTarget:F1} single, {damage.AreaPerTarget:F1} area per target");
Console.WriteLine($"expected actions: {baseline.Sum(window => window.AliveAtAction):F2}");
var options = new List<OpeningOption>();
double retargetShare = valuation.GetProperty("vulnerableRetargetShare").GetDouble();
double vulnerableHpCoefficient = valuation.GetProperty("vulnerableHpCoefficient").GetDouble();
double fullBlockChance = entry.TryGetProperty("fullBlockChance", out var blockChance) ? blockChance.GetDouble() : 0;
double? ValueState(string id, OpeningState state, IReadOnlyList<OpeningPick> previous)
{
    bool primary = id == monsterId;
    var ally = primary ? default : allyInputs.Single(input => input.GetProperty("id").GetString() == id);
    var ownStates = previous.Where(pick => pick.MonsterId == id).Select(pick => pick.State).ToArray();
    var allyStates = previous.Where(pick => pick.MonsterId != id).GroupBy(pick => pick.MonsterId)
        .ToDictionary(group => group.Key, group => group.Select(pick => pick.State).ToArray());
    var before = primary && previous.Count == 0 ? baseline :
        SurvivalForecast.Predict(enemies, id, damage, scenarios, horizon, firstPlayerWindow,
            vulnerableDamageIncrease: vulnerableIncrease, additionalOpening: ownStates,
            vulnerableRetargetShare: retargetShare, allyOpening: allyStates);
    var after = SurvivalForecast.Predict(enemies, id, damage, scenarios, horizon, firstPlayerWindow, opening: state,
        vulnerableDamageIncrease: vulnerableIncrease, additionalOpening: ownStates,
        vulnerableRetargetShare: retargetShare, allyOpening: allyStates);
    var expected = primary ? moves : [ally.GetProperty("expectedMove").Deserialize<ExpectedMove>(inputs)!];
    return OpeningValuation.Evaluate(state, before, after, expected, discount,
        sample.GetProperty("artifactValue").GetDouble(), sample.GetProperty("artifactChancePerTargetedWindow").GetDouble(),
        weakReduction, healCoefficient, blockCoefficient, valuation.GetProperty("effectiveHpCoefficient").GetDouble(),
        vulnerableHpCoefficient, primary ? fullBlockChance : 0);
}
foreach (var option in ProfileSetting("openingStates").EnumerateArray())
{
    string id = option.GetString()!;
    if (id == "STUN" && enemies[0].FixedOpening)
        continue;
    var state = new OpeningState(id, valuation.GetProperty("amounts").GetProperty(id).GetInt32());
    double? value = ValueState(monsterId, state, []);
    options.Add(new OpeningOption(state, value));
    Console.WriteLine($"{id,-15} {value?.ToString("+0.00;-0.00;0.00") ?? "unknown"}");
}

var openingRandom = new Random(seed);
var actionRandom = new Random(seed ^ 0x649AAB81);
var imbalanceRandom = new Random(seed ^ 0x4B82C097);
var openingWeights = valuation.GetProperty("weights");
double Budget(double actionValue) => Math.Min(valuation.GetProperty("maxAbsoluteValue").GetDouble(),
    valuation.GetProperty("maxActionValueShare").GetDouble() * actionValue);
var choicesByMonster = new Dictionary<string, IReadOnlyList<OpeningOption>> { [monsterId] = options };
var budgets = new Dictionary<string, double> { [monsterId] = Budget(profile.ActionValue) };
foreach (var ally in allyInputs.Where(input => input.TryGetProperty("openingStates", out _) && input.TryGetProperty("expectedMove", out _)))
{
    string id = ally.GetProperty("id").GetString()!;
    var snapshot = enemies.Single(enemy => enemy.Id == id);
    choicesByMonster[id] = ally.GetProperty("openingStates").EnumerateArray().Select(option => option.GetString()!)
        .Where(state => !(state == "STUN" && snapshot.FixedOpening) &&
            !(state is "DOOM" or "DEMISE" && snapshot.ScriptedDeath))
        .Select(state => new OpeningState(state, valuation.GetProperty("amounts").GetProperty(state).GetInt32()))
        .Select(state => new OpeningOption(state, ValueState(id, state, []))).ToArray();
    budgets[id] = Budget(ally.GetProperty("expectedMove").GetProperty("value").GetDouble());
}
var selected = OpeningValuation.Draw(
    choicesByMonster, budgets,
    new OpeningDraws(openingWeights.GetProperty("none").GetDouble(), openingWeights.GetProperty("positive").GetDouble(),
        openingWeights.GetProperty("negative").GetDouble(), valuation.GetProperty("attempts").GetInt32(),
        valuation.GetProperty("maxPerMonster").GetInt32(), valuation.GetProperty("maxEncounterAbsoluteValue").GetDouble()),
    openingRandom, ValueState);
var ownOpening = selected.Where(pick => pick.MonsterId == monsterId).ToArray();
double openingValue = ownOpening.Sum(pick => pick.Value);
Console.WriteLine("opening: " + (selected.Count == 0 ? "NONE" : string.Join(", ", selected.Select(pick =>
    $"{pick.MonsterId}:{pick.State.Id} ({pick.Value:+0.00;-0.00;0.00})"))) +
    $" | encounter net {selected.Sum(pick => pick.Value):+0.00;-0.00;0.00}, absolute {selected.Sum(pick => Math.Abs(pick.Value)):F2}");

var planner = new ActionPlanner(settings);
var balances = planner.Start(profile, openingValue);
Console.WriteLine($"opening pool: {balances.ActionValue:F2} (starts at {planner.Start(profile).ActionValue:F2} before state)");
var catalog = new ActionCatalog(new ActionValuationSettings(
    blockCoefficient,
    actions.GetProperty("weak").GetDouble(),
    weakReduction, vulnerableIncrease,
    actions.GetProperty("cardSlotValue").GetDouble(), discount,
    actions.GetProperty("maxAddedStrength").GetInt32(),
    actions.GetProperty("maxInsertedCards").GetInt32(),
    actions.GetProperty("playerDebuffCooldownTurns").GetInt32(),
    horizon, actions.GetProperty("pureBlockCooldownTurns").GetInt32(),
    actions.GetProperty("minimumBaseDamagePerHit").GetInt32(),
    actions.GetProperty("amounts").EnumerateObject().ToDictionary(
        field => Enum.Parse<EffectKind>(field.Name, ignoreCase: true),
        field => field.Value.Deserialize<int[]>()!),
    actions.GetProperty("attack").GetDouble(),
    actions.GetProperty("maxAddedRitual").GetInt32(), actions.GetProperty("tangledEnergyValue").GetDouble(),
    actions.GetProperty("ringingCardValue").GetDouble(), actions.GetProperty("burnPenalty").GetDouble(),
    actions.GetProperty("cardPlacementValues").Deserialize<StatusPlacementValues>(inputs),
    actions.GetProperty("maxHardCardLimits").GetInt32(), actions.GetProperty("handBurnDamageChance").GetDouble()));
var history = new ActionHistory();
bool previousAttack = false;
var turnContext = sample.GetProperty("turnContext").Deserialize<ActionContext>(inputs)!;
bool hasFallback = sample.TryGetProperty("fallback", out var fallback);
int fallbackHits = hasFallback && fallback.TryGetProperty("attackHits", out var fallbackCount)
    ? fallbackCount.GetInt32() : (profile.AttackFrequency > 0 ? 1 : 0);
double fallbackActionValue = hasFallback && fallback.TryGetProperty("actionValue", out var fallbackValue)
    ? fallbackValue.GetDouble() : profile.ActionValue;
string fallbackScore = hasFallback && fallback.TryGetProperty("actualScore", out var resolvedFallback)
    ? resolvedFallback.GetDouble().ToString("F1") : "n/a";
Console.WriteLine("\nAction-pool trace (conditioning on continued survival):");
for (int turn = 1; turn <= sample.GetProperty("traceTurns").GetInt32(); turn++)
{
    int strength = turnContext.Strength + history.AddedStrength + history.RitualStrength + ownOpening.Sum(pick =>
        pick.State.Id switch
        {
            "STRENGTH" or "STRENGTH_DOWN" => pick.State.Amount,
            "RITUAL" => (turn - 1) * pick.State.Amount,
            _ => 0
        });
    bool weak = turnContext.Weakened || ownOpening.Any(pick => pick.State.Id == "WEAK" && turn <= pick.State.Amount);
    bool imbalanced = ownOpening.Any(pick => pick.State.Id == "IMBALANCED");
    bool skipFromImbalance = imbalanced && previousAttack && imbalanceRandom.NextDouble() < fullBlockChance;
    var context = turnContext with { Strength = strength, Weakened = weak,
        AllowHardCardLimit = turnContext.AllowHardCardLimit && turn >= actions.GetProperty("hardCardLimitFirstTurn").GetInt32() };
    var candidates = catalog.Generate(profile, context, history, turn);
    var action = planner.Plan(balances, profile, candidates, actionRandom,
        canRandomize: !skipFromImbalance && !ownOpening.Any(pick => pick.State.Id == "STUN" &&
            turn >= enemies[0].FirstEnemyAction && turn < enemies[0].FirstEnemyAction + pick.State.Amount));
    bool usedFallback = action.CanRandomize && action.Action is null;
    balances = usedFallback
        ? planner.Settle(action.Granted, fallbackActionValue, fallbackHits)
        : action.After;
    if (action.Action is not null)
        history = history.Record(action.Action, turn);
    else
        history = history with { RitualStrength = history.RitualStrength + history.AddedRitual };
    previousAttack = action.Action?.IsAttack ?? (usedFallback && fallbackHits > 0);
    string moveName = action.Action?.Id ?? (usedFallback ? "vanilla fallback" : skipFromImbalance ? "imbalanced" : "skipped");
    string actualScore = usedFallback ? fallbackScore : (action.Action?.ActualScore ?? 0).ToString("F1");
    Console.WriteLine(
        $"{turn,2} {moveName,-16} panel {action.Action?.Damage.ToString("F0") ?? (usedFallback ? "n/a" : "0"),3}  " +
        $"actual score {actualScore,4}  " +
        $"action cost {(action.Action?.Value ?? (usedFallback ? fallbackActionValue : 0)):F2}  " +
        $"pool {action.Granted.ActionValue:F2} -> {balances.ActionValue:F2}, " +
        $"desire {action.Granted.AttackDesire:F2} -> {balances.AttackDesire:F2}, " +
        $"hits {action.Action?.AttackHits ?? (usedFallback ? fallbackHits : 0)}");
}

JsonElement ProfileSetting(string name) => entry.TryGetProperty(name, out var value)
    ? value : defaults.GetProperty(name);
