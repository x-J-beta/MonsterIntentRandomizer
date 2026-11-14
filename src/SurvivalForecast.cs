namespace MonsterIntentRandomizer;

public sealed record EnemySnapshot(
    string Id, double Hp, double MaxHp, double Block, double Priority,
    int FirstPlayerWindow = 1, int FirstEnemyAction = 1, bool Targetable = true,
    bool FixedOpening = false, bool ScriptedDeath = false);

public sealed record DamageEstimate(double SingleTarget, double AreaPerTarget, double HitsPerTarget);
public sealed record TargetingScenario(double Weight, double FocusShare, double AreaMultiplier, double DamageMultiplier = 1);
public sealed record OpeningState(string Id, int Amount);
public sealed record ForecastWindow(
    double AliveAtAction, double AliveAtTurnEnd, double IncomingAttackHits,
    double IncomingAttackDamage, double ExtraBlockUsed, double Healing,
    double RemovedEnemyHp, bool Truncated, double VulnerableBonusDamage = 0);

public static class SurvivalForecast
{
    public static ForecastWindow[] Predict(
        IReadOnlyList<EnemySnapshot> enemies, string targetId, DamageEstimate damage,
        IReadOnlyList<TargetingScenario> scenarios, int horizon, double firstPlayerWindowFraction = 1,
        OpeningState? opening = null, double vulnerableDamageIncrease = 0.5,
        IReadOnlyList<OpeningState>? additionalOpening = null, double vulnerableRetargetShare = 0,
        IReadOnlyDictionary<string, OpeningState[]>? allyOpening = null)
    {
        int target = Enumerable.Range(0, enemies.Count).FirstOrDefault(i => enemies[i].Id == targetId, -1);
        if (target < 0)
        {
            throw new ArgumentException("Forecast target is missing.", nameof(targetId));
        }
        EnemySnapshot subject = enemies[target];
        OpeningState[] states = opening is null ? (additionalOpening ?? []).ToArray() :
            [opening, .. additionalOpening ?? []];
        var byEnemy = enemies.Select((enemy, index) => index == target ? states :
            allyOpening?.GetValueOrDefault(enemy.Id) ?? []).ToArray();
        int Amount(int index, string id) => byEnemy[index].Where(state => state.Id == id).Sum(state => state.Amount);
        if ((Amount(target, "STUN") > 0 && subject.FixedOpening) ||
            ((Amount(target, "DOOM") > 0 || Amount(target, "DEMISE") > 0) && subject.ScriptedDeath))
        {
            throw new ArgumentException("Opening state conflicts with the monster script.", nameof(opening));
        }

        var result = Enumerable.Repeat(new ForecastWindow(0, 0, 0, 0, 0, 0, 0, false), horizon).ToArray();
        foreach (var scenario in scenarios)
        {
            double[] hp = enemies.Select(enemy => enemy.Hp).ToArray();
            int[] regen = Enumerable.Range(0, enemies.Count).Select(i => Amount(i, "REGEN")).ToArray();
            int[] plating = Enumerable.Range(0, enemies.Count).Select(i => Amount(i, "PLATING")).ToArray();
            int[] vulnerable = Enumerable.Range(0, enemies.Count).Select(i => Amount(i, "VULNERABLE")).ToArray();
            int[] demise = Enumerable.Range(0, enemies.Count).Select(i => Amount(i, "DEMISE")).ToArray();
            int[] doom = Enumerable.Range(0, enemies.Count).Select(i => Amount(i, "DOOM")).ToArray();
            int stun = Amount(target, "STUN");
            int[] nextPlatingBlock = plating.ToArray();
            for (int turn = 1; turn <= horizon; turn++)
            {
                double playerFraction = turn == 1 ? firstPlayerWindowFraction : 1;
                int[] targets = Enumerable.Range(0, enemies.Count)
                    .Where(i => hp[i] > 0 && enemies[i].Targetable && turn >= enemies[i].FirstPlayerWindow)
                    .OrderByDescending(i => enemies[i].Priority).ThenBy(i => hp[i])
                    .ThenBy(i => enemies[i].Id, StringComparer.Ordinal).ToArray();
                double targetHits = 0;
                double incoming = 0;
                double extraBlockUsed = 0;
                double healed = 0;
                double removedHp = 0;
                double vulnerableBonus = 0;
                double focusDamage = 0;
                foreach (int index in targets)
                {
                    double share = (1 - scenario.FocusShare) / targets.Length;
                    if (index == targets[0])
                    {
                        share += scenario.FocusShare;
                    }
                    double single = damage.SingleTarget * share * playerFraction * scenario.DamageMultiplier + focusDamage;
                    double area = damage.AreaPerTarget * scenario.AreaMultiplier * playerFraction * scenario.DamageMultiplier;
                    double received = single + area;
                    double block = turn == 1 ? enemies[index].Block : 0;
                    if (index == target)
                    {
                        if (turn <= vulnerable[index])
                        {
                            // A player may redirect some single-target damage toward the vulnerable enemy.
                            double redirected = index == targets[0] ? 0 :
                                damage.SingleTarget * playerFraction * scenario.DamageMultiplier *
                                (1 - share) * vulnerableRetargetShare;
                            double potentialBase = received + redirected;
                            double potential = potentialBase * (1 + vulnerableDamageIncrease);
                            vulnerableBonus = Math.Max(0,
                                Math.Min(hp[index], Math.Max(0, potential - block - nextPlatingBlock[index])) -
                                Math.Min(hp[index], Math.Max(0, potentialBase - block - nextPlatingBlock[index])));
                        }
                    }
                    if (turn <= vulnerable[index])
                        received *= 1 + vulnerableDamageIncrease;
                    if (index == target)
                    {
                        incoming = Math.Min(received, hp[index] + block + nextPlatingBlock[index]);
                        // Approximate hit count scales with the damage assigned to this creature.
                        double hitRate = damage.HitsPerTarget / Math.Max(1, damage.SingleTarget + damage.AreaPerTarget);
                        targetHits = incoming * hitRate;
                        extraBlockUsed = Math.Min(nextPlatingBlock[index], Math.Max(0, received - block));
                    }
                    block += nextPlatingBlock[index];
                    double availableHp = hp[index] + block;
                    focusDamage = Math.Min(single, Math.Max(0, received - availableHp));
                    hp[index] = Math.Max(0, hp[index] - Math.Max(0, received - block));
                }

                bool ableToAct = hp[target] > 0 && turn >= enemies[target].FirstEnemyAction;
                bool aliveAtAction = ableToAct && turn >= enemies[target].FirstEnemyAction + stun;
                for (int index = 0; index < enemies.Count; index++)
                {
                    bool active = hp[index] > 0 && turn >= enemies[index].FirstEnemyAction;
                    if (!active)
                        continue;
                    double heal = regen[index] > 0 ? Math.Min(regen[index]--, enemies[index].MaxHp - hp[index]) : 0;
                    hp[index] += heal;
                    nextPlatingBlock[index] = plating[index] > 0 ? plating[index]-- : 0;
                    double loss = Math.Min(hp[index], demise[index]);
                    hp[index] -= loss;
                    if (doom[index] > 0 && hp[index] <= doom[index])
                    {
                        loss += hp[index];
                        hp[index] = 0;
                    }
                    if (index == target)
                    {
                        healed = heal;
                        removedHp = loss;
                    }
                }

                double w = scenario.Weight;
                ForecastWindow prior = result[turn - 1];
                result[turn - 1] = new ForecastWindow(
                    prior.AliveAtAction + (aliveAtAction ? w : 0),
                    prior.AliveAtTurnEnd + (ableToAct ? w : 0),
                    prior.IncomingAttackHits + targetHits * w,
                    prior.IncomingAttackDamage + incoming * w,
                    prior.ExtraBlockUsed + extraBlockUsed * w,
                    prior.Healing + healed * w,
                    prior.RemovedEnemyHp + removedHp * w,
                    prior.Truncated || (turn == horizon && hp[target] > 0 && w > 0),
                    prior.VulnerableBonusDamage + vulnerableBonus * w);
            }
        }
        return result;
    }

    public static DamageEstimate BlendDamage(DamageEstimate stagePrior, IEnumerable<DamageEstimate> recent, double priorWeight)
    {
        DamageEstimate[] observed = recent.ToArray();
        double total = priorWeight + observed.Length;
        return new DamageEstimate(
            (stagePrior.SingleTarget * priorWeight + observed.Sum(round => round.SingleTarget)) / total,
            (stagePrior.AreaPerTarget * priorWeight + observed.Sum(round => round.AreaPerTarget)) / total,
            (stagePrior.HitsPerTarget * priorWeight + observed.Sum(round => round.HitsPerTarget)) / total);
    }

    public static ForecastWindow[] FromExpectedSurvival(double expectedActions, int horizon) =>
        Enumerable.Range(0, horizon).Select(index =>
        {
            double alive = Math.Clamp(expectedActions - index, 0, 1);
            return new ForecastWindow(alive, alive, 0, 0, 0, 0, 0,
                index == horizon - 1 && expectedActions >= horizon);
        }).ToArray();
}
