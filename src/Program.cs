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
using var simulation = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "prototype-scenarios.json")));
var sample = simulation.RootElement;
int seed = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : sample.GetProperty("defaultSeed").GetInt32();
string monsterId = (args.Length > 1 ? args[1] : sample.GetProperty("defaultMonsterId").GetString()!).ToUpperInvariant();
string ascension = args.Length > 2 ? args[2] : sample.GetProperty("defaultActionValueTier").GetString()!;
using var profiles = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "monster-profiles.json")));
using var values = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "action-values.json")));
using var states = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "initial-states.json")));
var settings = values.RootElement.GetProperty("pools").Deserialize<PoolSettings>(inputs)!;
var entry = profiles.RootElement.GetProperty("profiles").GetProperty(monsterId);
var draft = entry.GetProperty("draftProfile");
var hitWeights = draft.GetProperty("allowedHitCounts").EnumerateArray().ToDictionary(
    count => count.GetInt32(), count =>
    {
        string key = count.GetInt32() == 1 ? "single_attack" : "multi_attack";
        return draft.TryGetProperty("attackTemplateWeights", out var weights) &&
            weights.TryGetProperty(key, out var weight) ? weight.GetDouble() : 1;
    });
var profile = new MonsterProfile(
    draft.GetProperty("actionValue").GetProperty(ascension).GetDouble(),
    draft.GetProperty("actionVolatility").GetDouble(),
    draft.GetProperty("attackFrequency").GetDouble(),
    draft.GetProperty("nonAttackValueMultiplier").GetDouble(),
    draft.GetProperty("baseExpectedSurvivalTurns").GetDouble(),
    draft.GetProperty("intentWeights").EnumerateObject().ToDictionary(
        field => Enum.Parse<IntentStyle>(field.Name, ignoreCase: true), field => field.Value.GetDouble()),
    hitWeights,
    draft.GetProperty("basicEffects").EnumerateArray()
        .Select(effect => Enum.Parse<EffectKind>(effect.GetString()!)).ToArray());

var valuation = states.RootElement.GetProperty("valuation");
var effectValues = values.RootElement.GetProperty("effects");
double weakReduction = effectValues.GetProperty("weak").GetProperty("attackDamageReduction").GetDouble();
double vulnerableIncrease = effectValues.GetProperty("vulnerable").GetProperty("attackDamageIncrease").GetDouble();
double healCoefficient = effectValues.GetProperty("heal").GetProperty("coefficientPerEffectiveHp").GetDouble();
double blockCoefficient = effectValues.GetProperty("block").GetProperty("coefficientPerEffectiveBlock").GetDouble();
var forecast = valuation.GetProperty("survivalForecast");
var scenarios = forecast.GetProperty("scenarios").EnumerateArray().Select(scenario => new TargetingScenario(
    scenario.GetProperty("weight").GetDouble(), scenario.GetProperty("singleTargetFocusShare").GetDouble(),
    scenario.GetProperty("areaDamageMultiplier").GetDouble())).ToArray();
int horizon = forecast.GetProperty("maxEnemyWindows").GetInt32();
double discount = valuation.GetProperty("futureDiscountPerEnemyTurn").GetDouble();
double hp = entry.GetProperty("demoHp").GetDouble();
var enemies = new[]
{
    new EnemySnapshot(monsterId, hp, hp, sample.GetProperty("targetBlock").GetDouble(),
        sample.GetProperty("targetPriority").GetDouble(),
        FirstPlayerWindow: sample.GetProperty("targetFirstPlayerWindow").GetInt32(),
        FirstEnemyAction: sample.GetProperty("targetFirstEnemyAction").GetInt32(),
        FixedOpening: draft.TryGetProperty("preserveOpeningRole", out var opening) && opening.GetBoolean())
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
foreach (var option in draft.GetProperty("initialRandomStateOptions").EnumerateArray())
{
    string id = option.GetString()!;
    var candidate = states.RootElement.GetProperty("eligible").TryGetProperty(id, out var eligibleState)
        ? eligibleState : states.RootElement.GetProperty("conditional").GetProperty(id);
    var state = new OpeningState(id, candidate.TryGetProperty("amount", out var amount) ? amount.GetInt32() : 0);
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
var openingWeights = states.RootElement.GetProperty("sampling").GetProperty("defaultEncounterWeights");
double budget = Math.Min(valuation.GetProperty("maxAbsolutePressureDelta").GetDouble(),
    valuation.GetProperty("maxAbsoluteShareOfStandardActionValue").GetDouble() * profile.ActionValue);
var selected = OpeningValuation.ChooseTarget(
    new Dictionary<string, IReadOnlyList<OpeningOption>> { [monsterId] = options },
    new Dictionary<string, double> { [monsterId] = budget },
    openingWeights.GetProperty("positive").GetDouble(), openingWeights.GetProperty("negative").GetDouble(),
    openingWeights.GetProperty("none").GetDouble(), openingRandom);
Console.WriteLine($"opening: {selected?.State.Id ?? "NONE"} (value {selected?.Value ?? 0:+0.00;-0.00;0.00})");

var planner = new ActionPlanner(settings);
var balances = planner.Start(profile, selected?.Value ?? 0);
Console.WriteLine($"opening pool: {balances.ActionValue:F2} (starts at {planner.Start(profile).ActionValue:F2} before state)");
var caps = values.RootElement.GetProperty("generatedActionLimits");
var catalog = new ActionCatalog(new ActionValuationSettings(
    blockCoefficient,
    effectValues.GetProperty("weak").GetProperty("coefficientPerPreventedPlayerDamage").GetDouble(),
    weakReduction, vulnerableIncrease,
    caps.GetProperty("cardSlotValue").GetDouble(), discount,
    caps.GetProperty("maxAddedStrength").GetInt32(),
    caps.GetProperty("maxInsertedCards").GetInt32(),
    caps.GetProperty("playerDebuffCooldownTurns").GetInt32(),
    horizon, caps.GetProperty("pureBlockCooldownTurns").GetInt32(),
    caps.GetProperty("minimumBaseDamagePerHit").GetInt32(),
    caps.GetProperty("amounts").EnumerateObject().ToDictionary(
        field => Enum.Parse<EffectKind>(field.Name, ignoreCase: true),
        field => field.Value.Deserialize<int[]>()!),
    effectValues.GetProperty("attack").GetProperty("coefficientPerDamage").GetDouble()));
var history = new ActionHistory();
var turnContext = sample.GetProperty("turnContext").Deserialize<ActionContext>(inputs)!;
var fallback = sample.GetProperty("fallback");
int fallbackHits = fallback.GetProperty("attackHits").GetInt32();
double fallbackActionValue = fallback.TryGetProperty("actionValue", out var fallbackValue)
    ? fallbackValue.GetDouble() : profile.ActionValue;
string fallbackScore = fallback.TryGetProperty("panelDamage", out var fallbackDamage)
    ? fallbackDamage.GetDouble().ToString("F1") : "n/a";
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
        $"{turn,2} {moveName,-16} actual score {actualScore,4}  " +
        $"action cost {(action.Action?.Value ?? (usedFallback ? fallbackActionValue : 0)):F2}  " +
        $"pool {action.Granted.ActionValue:F2} -> {balances.ActionValue:F2}, " +
        $"desire {action.Granted.AttackDesire:F2} -> {balances.AttackDesire:F2}, " +
        $"hits {action.Action?.AttackHits ?? (usedFallback ? fallbackHits : 0)}");
}
