using System.Globalization;
using System.Text.Json;
using MonsterIntentRandomizer;

if (args.Length > 0 && args[0] == "--check")
{
    PrototypeChecks.Run();
    return;
}

int seed = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 42;
string monsterId = args.Length > 1 ? args[1].ToUpperInvariant() : "TOADPOLE";
string ascension = args.Length > 2 ? args[2] : "standard";
string folder = Path.Combine(Directory.GetCurrentDirectory(), "data");
var inputs = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
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
var forecast = valuation.GetProperty("survivalForecast");
var scenarios = forecast.GetProperty("scenarios").EnumerateArray().Select(scenario => new TargetingScenario(
    scenario.GetProperty("weight").GetDouble(), scenario.GetProperty("singleTargetFocusShare").GetDouble(),
    scenario.GetProperty("areaDamageMultiplier").GetDouble())).ToArray();
int horizon = forecast.GetProperty("maxEnemyWindows").GetInt32();
double discount = valuation.GetProperty("futureDiscountPerEnemyTurn").GetDouble();
double hp = entry.GetProperty("demoHp").GetDouble();
var enemies = new[]
{
    new EnemySnapshot("front", hp, hp, 0, 0,
        FixedOpening: draft.TryGetProperty("preserveOpeningRole", out var opening) && opening.GetBoolean()),
    new EnemySnapshot("rear", 23, 23, 0, 1)
};
// Reference output for one early Act 1 demonstration, until an encounter adapter supplies observations.
var damage = SurvivalForecast.BlendDamage(new DamageEstimate(10, 2, 2), [],
    forecast.GetProperty("stagePriorWeightInPlayerRounds").GetDouble());
var baseline = SurvivalForecast.Predict(enemies, "front", damage, scenarios, horizon);
double expectedHits = hitWeights.Sum(pair => pair.Key * pair.Value) / hitWeights.Values.Sum();
double expectedAction = profile.ActionValue *
    (profile.AttackFrequency + (1 - profile.AttackFrequency) * profile.NonAttackMultiplier);
double expectedHitDamage = Math.Max(1, Math.Round(profile.ActionValue / expectedHits));
var moves = new[] { new ExpectedMove(expectedAction, profile.AttackFrequency, expectedHitDamage, expectedHits) };
Console.WriteLine($"{monsterId} expected actions: {baseline.Sum(window => window.AliveAtAction):F2}");
var options = new List<OpeningOption>();
foreach (var option in draft.GetProperty("initialRandomStateOptions").EnumerateArray())
{
    string id = option.GetString()!;
    var candidate = states.RootElement.GetProperty("eligible").TryGetProperty(id, out var eligibleState)
        ? eligibleState : states.RootElement.GetProperty("conditional").GetProperty(id);
    var state = new OpeningState(id, candidate.TryGetProperty("amount", out var amount) ? amount.GetInt32() : 0);
    var modified = SurvivalForecast.Predict(enemies, "front", damage, scenarios, horizon, opening: state);
    double? value = OpeningValuation.Evaluate(state, baseline, modified, moves, discount);
    options.Add(new OpeningOption(state, value));
    Console.WriteLine($"{id,-15} {value?.ToString("+0.00;-0.00;0.00") ?? "unknown"}");
}

var openingRandom = new Random(seed);
var actionRandom = new Random(seed ^ 0x649AAB81);
var openingWeights = states.RootElement.GetProperty("sampling").GetProperty("defaultEncounterWeights");
double budget = Math.Min(valuation.GetProperty("maxAbsolutePressureDelta").GetDouble(),
    valuation.GetProperty("maxAbsoluteShareOfStandardActionValue").GetDouble() * profile.ActionValue);
var selected = OpeningValuation.ChooseTarget(
    new Dictionary<string, IReadOnlyList<OpeningOption>> { ["front"] = options },
    new Dictionary<string, double> { ["front"] = budget },
    openingWeights.GetProperty("positive").GetDouble(), openingWeights.GetProperty("negative").GetDouble(),
    openingWeights.GetProperty("none").GetDouble(), openingRandom);
Console.WriteLine($"opening: {selected?.State.Id ?? "NONE"} (value {selected?.Value ?? 0:+0.00;-0.00;0.00})");

var planner = new ActionPlanner(settings);
var balances = planner.Start(profile, selected?.Value ?? 0);
var effectValues = values.RootElement.GetProperty("effects");
var caps = values.RootElement.GetProperty("generatedActionLimits");
var catalog = new ActionCatalog(new ActionValuationSettings(
    effectValues.GetProperty("block").GetProperty("coefficientPerEffectiveBlock").GetDouble(),
    effectValues.GetProperty("weak").GetProperty("coefficientPerPreventedPlayerDamage").GetDouble(),
    effectValues.GetProperty("weak").GetProperty("attackDamageReduction").GetDouble(),
    effectValues.GetProperty("vulnerable").GetProperty("attackDamageIncrease").GetDouble(),
    caps.GetProperty("cardSlotValue").GetDouble(), discount,
    caps.GetProperty("maxAddedStrength").GetInt32(),
    caps.GetProperty("maxInsertedCards").GetInt32(),
    caps.GetProperty("playerDebuffCooldownTurns").GetInt32(),
    caps.GetProperty("minimumAttackValueShare").GetDouble()));
var history = new ActionHistory();
Console.WriteLine("\nAction-pool trace (conditioning on continued survival):");
for (int turn = 1; turn <= 10; turn++)
{
    string? stateId = selected?.State.Id;
    int amount = selected?.State.Amount ?? 0;
    int strength = history.AddedStrength + (stateId switch
    {
        "STRENGTH" or "STRENGTH_DOWN" => amount,
        "RITUAL" => (turn - 1) * amount,
        _ => 0
    });
    bool weak = stateId == "WEAK" && turn <= amount;
    var context = new ActionContext(8, 12, 10, strength, weak, DazedDraws: 0.6, WoundDraws: 0.8);
    var candidates = catalog.Generate(profile, context, history, turn);
    var action = planner.Plan(balances, profile, candidates, actionRandom,
        canRandomize: stateId != "STUN" || turn > 1);
    balances = action.CanRandomize && action.Action is null
        ? planner.Settle(action.Granted, profile.ActionValue, true)
        : action.After;
    if (action.Action is not null)
        history = history.Record(action.Action, turn);
    string moveName = action.Action?.Id ?? (action.CanRandomize ? "vanilla fallback" : "skipped");
    Console.WriteLine(
        $"{turn,2} {moveName,-16} damage {action.Action?.Damage ?? 0,2}  " +
        $"actual score {action.Action?.ActualValue ?? action.Action?.Value ?? 0:F1}  " +
        $"pool {action.Granted.ActionValue:F2} -> {balances.ActionValue:F2} " +
        $"(opening {action.OpeningAdjustmentApplied:+0.00;-0.00;0.00}), " +
        $"desire {action.Granted.AttackDesire:F2} -> {balances.AttackDesire:F2}");
}
