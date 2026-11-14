namespace MonsterIntentRandomizer;

public sealed record EnemySnapshot(
    string Id, double Hp, double MaxHp, double Block, double Priority,
    int FirstPlayerWindow = 1, int FirstEnemyAction = 1, bool Targetable = true,
    bool FixedOpening = false, bool ScriptedDeath = false);

public sealed record DamageEstimate(double SingleTarget, double AreaPerTarget, double HitsPerTarget);
public sealed record TargetingScenario(double Weight, double FocusShare, double AreaMultiplier);
public sealed record OpeningState(string Id, int Amount);
public sealed record ForecastWindow(
    double AliveAtAction, double AliveAtTurnEnd, double IncomingAttackHits,
    double IncomingAttackDamage, double ExtraBlockUsed, double Healing,
    double RemovedEnemyHp, bool Truncated);

public static class SurvivalForecast
{
    public static ForecastWindow[] Predict(
        IReadOnlyList<EnemySnapshot> enemies, string targetId, DamageEstimate damage,
        IReadOnlyList<TargetingScenario> scenarios, int horizon, double firstPlayerWindowFraction = 1,
        OpeningState? opening = null)
    {
        int target = Enumerable.Range(0, enemies.Count).FirstOrDefault(i => enemies[i].Id == targetId, -1);
        if (target < 0)
        {
            throw new ArgumentException("Forecast target is missing.", nameof(targetId));
        }
        EnemySnapshot subject = enemies[target];
        if ((opening?.Id == "STUN" && subject.FixedOpening) ||
            (opening?.Id is "DOOM" or "DEMISE" && subject.ScriptedDeath))
        {
            throw new ArgumentException("Opening state conflicts with the monster script.", nameof(opening));
        }

        var result = Enumerable.Repeat(new ForecastWindow(0, 0, 0, 0, 0, 0, 0, false), horizon).ToArray();
        foreach (var scenario in scenarios)
        {
            double[] hp = enemies.Select(enemy => enemy.Hp).ToArray();
            int regen = opening?.Id == "REGEN" ? opening.Amount : 0;
            int plating = opening?.Id == "PLATING" ? opening.Amount : 0;
            double nextPlatingBlock = plating;
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
                double focusDamage = 0;
                foreach (int index in targets)
                {
                    double share = (1 - scenario.FocusShare) / targets.Length;
                    if (index == targets[0])
                    {
                        share += scenario.FocusShare;
                    }
                    double single = damage.SingleTarget * share * playerFraction + focusDamage;
                    double area = damage.AreaPerTarget * scenario.AreaMultiplier * playerFraction;
                    double received = single + area;
                    double block = turn == 1 ? enemies[index].Block : 0;
                    if (index == target)
                    {
                        if (opening?.Id == "VULNERABLE" && turn <= opening.Amount)
                        {
                            received *= 1.5;
                        }
                        incoming = Math.Min(received, hp[index] + block + nextPlatingBlock);
                        // Approximate hit count scales with the damage assigned to this creature.
                        double hitRate = damage.HitsPerTarget / Math.Max(1, damage.SingleTarget + damage.AreaPerTarget);
                        targetHits = incoming * hitRate;
                        extraBlockUsed = Math.Min(nextPlatingBlock, Math.Max(0, received - block));
                        block += nextPlatingBlock;
                    }
                    double availableHp = hp[index] + block;
                    focusDamage = Math.Min(single, Math.Max(0, received - availableHp));
                    hp[index] = Math.Max(0, hp[index] - Math.Max(0, received - block));
                }

                bool ableToAct = hp[target] > 0 && turn >= enemies[target].FirstEnemyAction;
                bool aliveAtAction = ableToAct && !(opening?.Id == "STUN" && turn == enemies[target].FirstEnemyAction);
                if (ableToAct && regen > 0)
                {
                    healed = Math.Min(regen--, enemies[target].MaxHp - hp[target]);
                    hp[target] += healed;
                }
                nextPlatingBlock = ableToAct && plating > 0 ? plating-- : 0;
                if (ableToAct && opening?.Id == "DEMISE")
                {
                    removedHp = Math.Min(hp[target], opening.Amount);
                    hp[target] -= removedHp;
                }
                if (ableToAct && opening?.Id == "DOOM" && hp[target] <= opening.Amount)
                {
                    removedHp = hp[target];
                    hp[target] = 0;
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
                    prior.Truncated || (turn == horizon && hp[target] > 0 && w > 0));
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
