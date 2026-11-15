using System.Globalization;
using System.Text.Json;
using MonsterIntentRandomizer;

if (args.Length > 0 && args[0] == "--check")
{
    PrototypeChecks.Run();
    return;
}

if (args.Length > 0 && args[0] == "--calibrate-effects")
{
    string configDir = args.Length > 1 ? args[1] : "data";
    int seeds = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 320;
    if (seeds < 1)
        throw new ArgumentOutOfRangeException(nameof(seeds));
    using var calibrationSettings = JsonDocument.Parse(File.ReadAllText(Path.Combine(configDir, "settings.json")));
    using var calibrationProfiles = JsonDocument.Parse(File.ReadAllText(Path.Combine(configDir, "monster-profiles.json")));
    EffectCalibration.Run(new RuleConfiguration(calibrationSettings.RootElement, calibrationProfiles.RootElement), seeds);
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
var profile = rules.PoolSettings.Adjust(configured.AtAscension(ascension == "deadlyEnemies"));
if (!configured.Enabled)
{
    Console.WriteLine($"{monsterId}: original actions retained (enabled is false).");
    return;
}

int rooms = args.Length > 5 ? int.Parse(args[5], CultureInfo.InvariantCulture) :
    sample.TryGetProperty("rooms", out var roomCount) ? roomCount.GetInt32() : 0;
double hp = entry.GetProperty("hp").GetDouble();
var allyInputs = sample.GetProperty("allies").EnumerateArray().ToArray();
var enemies = new[] { new EnemySnapshot(monsterId, hp, hp, 0, 0,
    FirstEnemyAction: sample.GetProperty("targetFirstEnemyAction").GetInt32(), FixedOpening: configured.FixedOpening) }
    .Concat(allyInputs.Select(ally => ally.Deserialize<EnemySnapshot>(inputs)!)).ToArray();
double allyHp = enemies.Skip(1).Sum(enemy => enemy.Hp);
profile = profile with
{
    BaseExpectedSurvivalTurns = rules.PlayerAction.Survival(hp, allyHp, rooms),
    PlayerActionValue = rules.PlayerAction.At(rooms),
    CurrentHp = hp,
    RoomsEntered = rooms + 1
};
string stage = args.Length > 4 ? args[4] : entry.TryGetProperty("stage", out var profileStage)
    ? profileStage.GetString()! : sample.GetProperty("defaultStage").GetString()!;
Console.WriteLine($"seed {seed}, {monsterId}, {ascension}, {stage}; target {profile.AttackFrequency:F2} attack hits/turn");
Console.WriteLine($"player action value: {rules.PlayerAction.At(rooms):F2}, expected actions: {profile.BaseExpectedSurvivalTurns:F2}");
int act = int.Parse(stage.AsSpan(3), CultureInfo.InvariantCulture);
OpeningOption[] Options(EnemySnapshot enemy, IEnumerable<string> states, MonsterProfile owner)
{
    return states
        .Where(id => !(id == "STUN" && enemy.FixedOpening) &&
            !(id is "DOOM" or "DEMISE" && enemy.ScriptedDeath) && rules.OpeningValues[id].MinAct <= act)
        .Select(id => OpeningValuation.Option(id, rules.OpeningValues[id], owner, rooms)).ToArray();
}
var options = Options(enemies[0], rules.OpeningStatesFor(configured), profile);
foreach (var option in options)
    Console.WriteLine($"{option.State.Id,-19} " + string.Join(", ", option.Doses!.Select(dose =>
        $"{dose.State.Amount}: {dose.Value:+0.00;-0.00;0.00}")));

var openingRandom = new Random(seed);
var choicesByMonster = new Dictionary<string, IReadOnlyList<OpeningOption>> { [monsterId] = options };
var actionValues = new Dictionary<string, double> { [monsterId] = profile.ActionValue };
foreach (var ally in allyInputs.Where(input => input.TryGetProperty("actionValue", out _)))
{
    string id = ally.GetProperty("id").GetString()!;
    actionValues[id] = ally.GetProperty("actionValue").GetDouble() * rules.PoolSettings.ActionValueMultiplier;
    if (ally.TryGetProperty("openingStates", out var allyStates))
        choicesByMonster[id] = Options(enemies.Single(enemy => enemy.Id == id), RuleConfiguration.Read<string[]>(allyStates),
            profile with
            {
                ActionValue = actionValues[id],
                AttackFrequency = ally.TryGetProperty("attackFrequency", out var frequency) ? frequency.GetDouble() : 1,
                BaseExpectedSurvivalTurns = rules.PlayerAction.Survival(enemies.Single(enemy => enemy.Id == id).Hp,
                    enemies.Where(enemy => enemy.Id != id).Sum(enemy => enemy.Hp), rooms),
                CurrentHp = enemies.Single(enemy => enemy.Id == id).Hp
            });
}
var selected = OpeningValuation.Draw(choicesByMonster, actionValues, rules.Draws, openingRandom);
var ownOpening = selected.Where(pick => pick.MonsterId == monsterId).ToArray();
double openingValue = ownOpening.Sum(pick => pick.Value);
Console.WriteLine("opening: " + (selected.Count == 0 ? "NONE" : string.Join(", ", selected.Select(pick =>
    $"{pick.MonsterId}:{pick.State.Id} {pick.State.Amount} ({pick.Value:+0.00;-0.00;0.00})"))) +
    $" | encounter net {selected.Sum(pick => pick.Value):+0.00;-0.00;0.00}, " +
    $"net limit {actionValues.Values.Sum() * rules.Draws.MaxEncounterValueMultiplier:F2}");

var planner = new ActionPlanner(rules.PoolSettings);
var balances = planner.Start(profile, openingValue);
Console.WriteLine($"opening pool: {balances.ActionValue:F2} (starts at {planner.Start(profile).ActionValue:F2} before state)");
bool hasFallback = sample.TryGetProperty("fallback", out var fallback);
int fallbackHits = hasFallback && fallback.TryGetProperty("attackHits", out var fallbackCount)
    ? fallbackCount.GetInt32() : (profile.AttackFrequency > 0 ? 1 : 0);
double fallbackActionValue = hasFallback && fallback.TryGetProperty("actionValue", out var fallbackValue)
    ? fallbackValue.GetDouble() : profile.ActionValue;
string fallbackScore = hasFallback && fallback.TryGetProperty("actualScore", out var resolvedFallback)
    ? resolvedFallback.GetDouble().ToString("F1") : "n/a";
Console.WriteLine("\nAction-pool trace (conditioning on continued survival):");
foreach (var turn in ActionSimulation.Trace(rules, profile, ownOpening, act, seed,
    sample.GetProperty("traceTurns").GetInt32(), configured.FullBlockChance,
    firstEnemyAction: enemies[0].FirstEnemyAction, fallbackValue: fallbackActionValue, fallbackHits: fallbackHits, rooms: rooms, enemyCount: enemies.Length))
{
    var action = turn.Plan;
    string moveName = action.Action?.Id ?? (turn.UsedFallback ? "vanilla fallback" : turn.Imbalanced ? "imbalanced" : "skipped");
    string actualScore = turn.UsedFallback ? fallbackScore : (action.Action?.ActualScore ?? 0).ToString("F1");
    Console.WriteLine(
        $"{turn.Round,2} {moveName,-16} panel {action.Action?.Damage.ToString("F0") ?? (turn.UsedFallback ? "n/a" : "0"),3}  " +
        $"actual score {actualScore,4}  " +
        $"action cost {turn.Cost:F2}  " +
        $"pool {action.Granted.ActionValue:F2} -> {turn.After.ActionValue:F2}, " +
        $"desire {action.Granted.AttackDesire:F2} -> {turn.After.AttackDesire:F2}, " +
        $"hits {turn.Hits}");
}

