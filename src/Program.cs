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
var rules = new RuleConfiguration(root, profiles.RootElement);
var entry = profiles.RootElement.GetProperty("monsters").GetProperty(monsterId);
var configured = rules.Monsters[monsterId];
if (ascension is not ("standard" or "deadlyEnemies"))
    throw new ArgumentException($"Unknown action value tier: {ascension}.");
var profile = configured.AtAscension(ascension == "deadlyEnemies");
if (!configured.Enabled)
{
    Console.WriteLine($"{monsterId}: original actions retained (enabled is false).");
    return;
}

var forecast = rules.Forecast;
var scenarios = forecast.GetProperty("scenarios").EnumerateArray().Select(scenario => new TargetingScenario(
    scenario.GetProperty("weight").GetDouble(), scenario.GetProperty("singleTargetFocusShare").GetDouble(),
    scenario.GetProperty("areaDamageMultiplier").GetDouble(),
    scenario.TryGetProperty("damageMultiplier", out var roll) ? roll.GetDouble() : 1)).ToArray();
int horizon = forecast.GetProperty("maxEnemyWindows").GetInt32();
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
Console.WriteLine($"seed {seed}, {monsterId}, {ascension}, {stage}; target {profile.AttackFrequency:F2} attack hits/turn");
Console.WriteLine($"player damage prior after blending: {damage.SingleTarget:F1} single, {damage.AreaPerTarget:F1} area per target");
Console.WriteLine($"expected actions: {baseline.Sum(window => window.AliveAtAction):F2}");
double fullBlockChance = entry.TryGetProperty("fullBlockChance", out var blockChance) ? blockChance.GetDouble() : 0;
OpeningOption[] Options(EnemySnapshot enemy, IEnumerable<string> states, double value)
{
    return states
        .Where(id => !(id == "STUN" && enemy.FixedOpening) &&
            !(id is "DOOM" or "DEMISE" && enemy.ScriptedDeath))
        .Select(id =>
        {
            var state = rules.OpeningValues[id];
            return new OpeningOption(new OpeningState(id, state.Amount), OpeningValuation.Evaluate(state, value));
        }).ToArray();
}
var options = Options(enemies[0], configured.OpeningStates, profile.ActionValue);
foreach (var option in options)
    Console.WriteLine($"{option.State.Id,-15} {option.Value:+0.00;-0.00;0.00}");

var openingRandom = new Random(seed);
var actionRandom = new Random(seed ^ 0x649AAB81);
var imbalanceRandom = new Random(seed ^ 0x4B82C097);
var choicesByMonster = new Dictionary<string, IReadOnlyList<OpeningOption>> { [monsterId] = options };
var actionValues = new Dictionary<string, double> { [monsterId] = profile.ActionValue };
foreach (var ally in allyInputs.Where(input => input.TryGetProperty("actionValue", out _)))
{
    string id = ally.GetProperty("id").GetString()!;
    actionValues[id] = ally.GetProperty("actionValue").GetDouble();
    if (ally.TryGetProperty("openingStates", out var allyStates))
        choicesByMonster[id] = Options(enemies.Single(enemy => enemy.Id == id), RuleConfiguration.Read<string[]>(allyStates), actionValues[id]);
}
var selected = OpeningValuation.Draw(choicesByMonster, actionValues, rules.Draws, openingRandom);
var ownOpening = selected.Where(pick => pick.MonsterId == monsterId).ToArray();
double openingValue = ownOpening.Sum(pick => pick.Value);
Console.WriteLine("opening: " + (selected.Count == 0 ? "NONE" : string.Join(", ", selected.Select(pick =>
    $"{pick.MonsterId}:{pick.State.Id} ({pick.Value:+0.00;-0.00;0.00})"))) +
    $" | encounter net {selected.Sum(pick => pick.Value):+0.00;-0.00;0.00}, " +
    $"net limit {actionValues.Values.Sum() * rules.Draws.MaxEncounterValueMultiplier:F2}");

var planner = new ActionPlanner(rules.PoolSettings);
var balances = planner.Start(profile, openingValue);
Console.WriteLine($"opening pool: {balances.ActionValue:F2} (starts at {planner.Start(profile).ActionValue:F2} before state)");
var catalog = new ActionCatalog(rules.Actions);
var history = new ActionHistory();
bool previousAttack = false;
int intangibleTurns = ownOpening.Where(pick => pick.State.Id == "INTANGIBLE").Sum(pick => pick.State.Amount);
var turnContext = rules.TurnPrior;
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
        AlreadyIntangible = turnContext.AlreadyIntangible || intangibleTurns > 0,
        AllowHardCardLimit = turnContext.AllowHardCardLimit && turn >= rules.HardCardLimitFirstTurn };
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
    intangibleTurns = Math.Max(0, intangibleTurns - 1) +
        (action.Action?.Effects?.Where(effect => effect.Kind == EffectKind.Intangible).Sum(effect => effect.Amount) ?? 0);
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

