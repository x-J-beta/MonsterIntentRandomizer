using System.Globalization;
using System.Text.Json;
using MonsterIntentRandomizer;

if (args.Length > 0 && args[0] == "--check")
{
    PrototypeChecks.Run();
    return;
}

int seed = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 42;
string folder = Path.Combine(Directory.GetCurrentDirectory(), "data");
var inputs = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
using var profiles = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "monster-profiles.json")));
using var values = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "action-values.json")));
using var states = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "initial-states.json")));
var settings = values.RootElement.GetProperty("pools").Deserialize<PoolSettings>(inputs)!;
var entry = profiles.RootElement.GetProperty("profiles").GetProperty("TOADPOLE");
var draft = entry.GetProperty("draftProfile");
var profile = new MonsterProfile(
    draft.GetProperty("actionValue").GetProperty("standard").GetDouble(),
    draft.GetProperty("actionVolatility").GetDouble(),
    draft.GetProperty("attackFrequency").GetDouble(),
    draft.GetProperty("nonAttackValueMultiplier").GetDouble(),
    draft.GetProperty("baseExpectedSurvivalTurns").GetDouble(),
    draft.GetProperty("intentWeights").EnumerateObject().ToDictionary(
        field => Enum.Parse<IntentStyle>(field.Name, ignoreCase: true), field => field.Value.GetDouble()));

var valuation = states.RootElement.GetProperty("valuation");
var forecast = valuation.GetProperty("survivalForecast");
var scenarios = forecast.GetProperty("scenarios").EnumerateArray().Select(scenario => new TargetingScenario(
    scenario.GetProperty("weight").GetDouble(), scenario.GetProperty("singleTargetFocusShare").GetDouble(),
    scenario.GetProperty("areaDamageMultiplier").GetDouble())).ToArray();
int horizon = forecast.GetProperty("maxEnemyWindows").GetInt32();
double discount = valuation.GetProperty("futureDiscountPerEnemyTurn").GetDouble();
var enemies = new[] { new EnemySnapshot("front", 23, 23, 0, 0, FixedOpening: true), new EnemySnapshot("rear", 23, 23, 0, 1) };
// Reference output for one early Act 1 demonstration, until an encounter adapter supplies observations.
var damage = SurvivalForecast.BlendDamage(new DamageEstimate(10, 2, 2), [],
    forecast.GetProperty("stagePriorWeightInPlayerRounds").GetDouble());
var baseline = SurvivalForecast.Predict(enemies, "front", damage, scenarios, horizon);
var moves = new[] { new ExpectedMove(7, 1, 7, 1), new ExpectedMove(7, profile.AttackFrequency, 4, 2) };
Console.WriteLine($"front expected actions: {baseline.Sum(window => window.AliveAtAction):F2}");
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
Console.WriteLine($"opening: {selected?.State.Id ?? "NONE"}");

var planner = new ActionPlanner(settings);
var balances = new Pools(profile.ActionValue * 0.5, 0);
var attackWeights = draft.GetProperty("attackTemplateWeights");
Console.WriteLine("\nAction-pool trace (conditioning on continued survival):");
for (int turn = 1; turn <= 10; turn++)
{
    string? stateId = selected?.State.Id;
    int amount = selected?.State.Amount ?? 0;
    int strength = stateId switch
    {
        "STRENGTH" or "STRENGTH_DOWN" => amount,
        "RITUAL" => (turn - 1) * amount,
        _ => 0
    };
    bool weak = stateId == "WEAK" && turn <= amount;
    var candidates = new[]
    {
        ActionCandidate.Attack("single 7", 1, 7, strength, weak, attackWeights.GetProperty("single_attack").GetDouble()),
        ActionCandidate.Attack("triple 3", 3, 3, strength, weak, attackWeights.GetProperty("multi_attack").GetDouble()),
        new ActionCandidate("self block", IntentStyle.Defend, false, 2.4, 0),
        new ActionCandidate("thorns", IntentStyle.Buff, false, 2.8, 0)
    };
    var action = planner.Plan(balances, profile, candidates, 9, actionRandom,
        canRandomize: stateId != "STUN" || turn > 1);
    balances = action.CanRandomize && action.Action is null
        ? planner.Settle(action.Granted, 7, true)
        : action.After;
    string moveName = action.Action?.Id ?? (action.CanRandomize ? "vanilla fallback" : "skipped");
    Console.WriteLine(
        $"{turn,2} {moveName,-16} damage {action.Action?.Damage ?? 0,2}  " +
        $"actual score {action.Action?.ActualValue ?? action.Action?.Value ?? 0:F1}  " +
        $"value {action.Granted.ActionValue:F2} -> {balances.ActionValue:F2}, " +
        $"desire {action.Granted.AttackDesire:F2} -> {balances.AttackDesire:F2}");
}
