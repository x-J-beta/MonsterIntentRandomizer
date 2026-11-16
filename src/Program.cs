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
    EffectCalibration.Run(new RuleConfiguration(calibrationSettings.RootElement, calibrationProfiles.RootElement, ReadMoves(configDir)), seeds);
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
var rules = new RuleConfiguration(root, profiles.RootElement, ReadMoves(folder));
monsterId = rules.MovePresets.ProfileId(monsterId);
var entry = profiles.RootElement.GetProperty("monsters").GetProperty(monsterId);
var configured = rules.Monsters[monsterId];
if (ascension is not ("standard" or "deadlyEnemies"))
    throw new ArgumentException($"Unknown action value tier: {ascension}.");
string? phaseId = args.Length > 6 ? args[6] : configured.DefaultPhase;
var phase = phaseId == null ? null : configured.Phases?.GetValueOrDefault(phaseId) ??
    throw new ArgumentException($"Unknown phase for {monsterId}: {phaseId}.");
var profile = rules.PoolSettings.Adjust(phase?.Apply(configured.Profile) ?? configured.Profile, ascension == "deadlyEnemies");
if (!configured.Enabled || phase?.Enabled == false ||
    rules.MovePresets.MoveIds(monsterId) is { Length: > 0 } known && !known.Any(configured.AllowsMove))
{
    Console.WriteLine($"{monsterId}: original actions retained.");
    return;
}

int rooms = args.Length > 5 ? int.Parse(args[5], CultureInfo.InvariantCulture) :
    sample.TryGetProperty("rooms", out var roomCount) ? roomCount.GetInt32() : 0;
double hp = phase?.Hp ?? entry.GetProperty("hp").GetDouble();
var allyInputs = sample.GetProperty("allies").EnumerateArray().ToArray();
var enemies = new[] { new SimulationEnemy(monsterId, hp,
    FirstEnemyAction: sample.GetProperty("targetFirstEnemyAction").GetInt32()) }
    .Concat(allyInputs.Select(ally => ally.Deserialize<SimulationEnemy>(inputs)!)).ToArray();
double allyHp = enemies.Skip(1).Sum(enemy => enemy.Hp);
profile = profile with
{
    BaseExpectedSurvivalTurns = rules.PlayerAction.Survival(hp, allyHp, rooms),
    PlayerActionValue = rules.PlayerAction.At(rooms),
    CurrentHp = hp,
    RoomsEntered = rooms + 1,
    MonstersBehind = enemies.Skip(1).Count(enemy => enemy.Hp > 0)
};
string stage = args.Length > 4 ? args[4] : entry.TryGetProperty("stage", out var profileStage)
    ? profileStage.GetString()! : sample.GetProperty("defaultStage").GetString()!;
Console.WriteLine($"seed {seed}, {monsterId}, {ascension}, {stage}; target {profile.AttackFrequency:F2} attack hits/turn");
if (phase != null)
    Console.WriteLine($"phase: {phase.Name}; action value: {profile.ActionValue:F2}");
if (phase?.Moves.Any(move => RuleConfiguration.UsesClawGrowth(monsterId, move)) == true)
    Console.WriteLine("attack desire income growth: +1 per randomized move");
Console.WriteLine($"player action value: {rules.PlayerAction.At(rooms):F2}, expected actions: {profile.BaseExpectedSurvivalTurns:F2}");
OpeningOption[] Options(SimulationEnemy enemy, IEnumerable<string> states, MonsterProfile owner)
{
    return states
        .Where(id => !(id is "DOOM" or "DEMISE" && enemy.ScriptedDeath))
        .Select(id => rules.OpeningOption(id, owner, rooms, monsterId: enemy.Id)).ToArray();
}
var sequence = rules.MovePresets.SequenceFor(monsterId, phaseId);
var options = Options(enemies[0], rules.OpeningStatesFor(monsterId, configured)
    .Where(id => id != "STUN" || configured.AllowsMove(sequence?.First)), profile);
foreach (var option in options)
    Console.WriteLine($"{option.State.Id,-19} " + string.Join(", ", option.Doses!.Select(dose =>
        $"{dose.State.Amount}{(dose.State.GeneratedCard is { } card ? $" [{card}]" : "")}: {dose.Value:+0.00;-0.00;0.00}")));

var combatRandom = CombatRandom.For(unchecked((uint)seed), rooms + 1);
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
                CurrentHp = enemies.Single(enemy => enemy.Id == id).Hp,
                MonstersBehind = enemies.SkipWhile(enemy => enemy.Id != id).Skip(1).Count(enemy => enemy.Hp > 0)
            });
}
var selected = OpeningValuation.Draw(choicesByMonster, actionValues, rules.Draws, combatRandom);
var ownOpening = selected.Where(pick => pick.MonsterId == monsterId).ToArray();
double openingValue = ownOpening.Sum(pick => pick.Value);
Console.WriteLine("opening: " + (selected.Count == 0 ? "NONE" : string.Join(", ", selected.Select(pick =>
    $"{pick.MonsterId}:{pick.State.Id} {pick.State.Amount}{(pick.State.GeneratedCard is { } card ? $" [{card}]" : "")} ({pick.Value:+0.00;-0.00;0.00})"))) +
    $" | encounter net {selected.Sum(pick => pick.Value):+0.00;-0.00;0.00}, " +
    $"net limit {actionValues.Values.Sum() * rules.Draws.MaxEncounterValueMultiplier * rules.Draws.Strength:F2}");

var planner = new ActionPlanner(rules.PoolSettings);
var balances = planner.Start(profile, openingValue);
Console.WriteLine($"opening pool: {balances.ActionValue:F2} (starts at {planner.Start(profile).ActionValue:F2} before state)");
Console.WriteLine("\nAction-pool trace (conditioning on continued survival):");
foreach (var turn in ActionSimulation.Trace(rules, monsterId, profile, selected, combatRandom,
    sample.GetProperty("traceTurns").GetInt32(), firstEnemyAction: enemies[0].FirstEnemyAction,
    rooms: rooms, enemyCount: enemies.Length, phaseId: phaseId))
{
    var action = turn.Plan;
    string moveName = action.Action?.Id ?? (turn.Imbalanced ? "imbalanced" : "skipped");
    if (turn.OriginalMove is { } original)
        moveName = action.Action == null ? $"{original} (vanilla)" : $"{original} -> {moveName}";
    Console.WriteLine(
        $"{turn.Round,2} {moveName,-16} panel {action.Action?.Damage ?? 0,3:F0}  " +
        $"actual score {action.Action?.ActualScore ?? 0,4:F1}  " +
        $"action cost {turn.Cost:F2}  " +
        $"pool {action.Granted.ActionValue:F2} -> {action.After.ActionValue:F2}, " +
        $"desire {action.Granted.AttackDesire:F2} -> {action.After.AttackDesire:F2}, " +
        $"hits {turn.Hits}");
}

static MonsterMovePresets ReadMoves(string folder)
{
    string path = Path.Combine(folder, "monster-moves.json");
    using var stream = File.Exists(path) ? File.OpenRead(path) :
        typeof(RuleConfiguration).Assembly.GetManifestResourceStream("monster-moves.json")!;
    using var document = JsonDocument.Parse(stream);
    return RuleConfiguration.Read<MonsterMovePresets>(document.RootElement);
}

internal sealed record SimulationEnemy(string Id, double Hp, int FirstEnemyAction = 1, bool ScriptedDeath = false);

