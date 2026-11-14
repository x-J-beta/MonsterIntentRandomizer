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
        field => Enum.Parse<EffectKind>(field.Name, ignoreCase: true), field => field.Value.GetDouble()) : null);
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
    scenario.GetProperty("areaDamageMultiplier").GetDouble())).ToArray();
int horizon = forecast.GetProperty("maxEnemyWindows").GetInt32();
double discount = forecast.GetProperty("discount").GetDouble();
double hp = entry.GetProperty("hp").GetDouble();
var enemies = new[]
{
    new EnemySnapshot(monsterId, hp, hp, sample.GetProperty("targetBlock").GetDouble(),
        sample.GetProperty("targetPriority").GetDouble(),
        FirstPlayerWindow: sample.GetProperty("targetFirstPlayerWindow").GetInt32(),
        FirstEnemyAction: sample.GetProperty("targetFirstEnemyAction").GetInt32(),
        FixedOpening: entry.TryGetProperty("fixedOpening", out var opening) && opening.GetBoolean())
}.Concat(sample.GetProperty("allies").Deserialize<EnemySnapshot[]>(inputs)!).ToArray();
// Reference output for one early Act 1 demonstration, until an encounter adapter supplies observations.
var damage = SurvivalForecast.BlendDamage(sample.GetProperty("stageDamagePrior").Deserialize<DamageEstimate>(inputs)!,
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
Console.WriteLine($"seed {seed}, {monsterId}, {ascension}; target {profile.AttackFrequency:F2} attack hits/turn");
Console.WriteLine($"expected actions: {baseline.Sum(window => window.AliveAtAction):F2}");
var options = new List<OpeningOption>();
foreach (var option in ProfileSetting("openingStates").EnumerateArray())
{
    string id = option.GetString()!;
    if (id == "STUN" && enemies[0].FixedOpening)
        continue;
    var state = new OpeningState(id, valuation.GetProperty("amounts").GetProperty(id).GetInt32());
    var modified = SurvivalForecast.Predict(enemies, monsterId, damage, scenarios, horizon, firstPlayerWindow, opening: state,
        vulnerableDamageIncrease: vulnerableIncrease);
    double? value = OpeningValuation.Evaluate(state, baseline, modified, moves, discount,
        sample.GetProperty("artifactValue").GetDouble(), sample.GetProperty("artifactChancePerTargetedWindow").GetDouble(),
        weakReduction, healCoefficient, blockCoefficient, valuation.GetProperty("effectiveHpCoefficient").GetDouble());
    options.Add(new OpeningOption(state, value));
    Console.WriteLine($"{id,-15} {value?.ToString("+0.00;-0.00;0.00") ?? "unknown"}");
}

var openingRandom = new Random(seed);
var actionRandom = new Random(seed ^ 0x649AAB81);
var openingWeights = valuation.GetProperty("weights");
double budget = Math.Min(valuation.GetProperty("maxAbsoluteValue").GetDouble(),
    valuation.GetProperty("maxActionValueShare").GetDouble() * profile.ActionValue);
var selected = OpeningValuation.ChooseTarget(
    new Dictionary<string, IReadOnlyList<OpeningOption>> { [monsterId] = options },
    new Dictionary<string, double> { [monsterId] = budget },
    openingWeights.GetProperty("positive").GetDouble(), openingWeights.GetProperty("negative").GetDouble(),
    openingWeights.GetProperty("none").GetDouble(), openingRandom);
Console.WriteLine($"opening: {selected?.State.Id ?? "NONE"} (value {selected?.Value ?? 0:+0.00;-0.00;0.00})");

var planner = new ActionPlanner(settings);
var balances = planner.Start(profile, selected?.Value ?? 0);
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
    actions.GetProperty("attack").GetDouble()));
var history = new ActionHistory();
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
    string? stateId = selected?.State.Id;
    int amount = selected?.State.Amount ?? 0;
    int strength = turnContext.Strength + history.AddedStrength + (stateId switch
    {
        "STRENGTH" or "STRENGTH_DOWN" => amount,
        "RITUAL" => (turn - 1) * amount,
        _ => 0
    });
    bool weak = turnContext.Weakened || (stateId == "WEAK" && turn <= amount);
    var context = turnContext with { Strength = strength, Weakened = weak };
    var candidates = catalog.Generate(profile, context, history, turn);
    var action = planner.Plan(balances, profile, candidates, actionRandom,
        canRandomize: stateId != "STUN" || turn > 1);
    bool usedFallback = action.CanRandomize && action.Action is null;
    balances = usedFallback
        ? planner.Settle(action.Granted, fallbackActionValue, fallbackHits)
        : action.After;
    if (action.Action is not null)
        history = history.Record(action.Action, turn);
    string moveName = action.Action?.Id ?? (usedFallback ? "vanilla fallback" : "skipped");
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
