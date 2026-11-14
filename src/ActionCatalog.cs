namespace MonsterIntentRandomizer;

public enum EffectKind { Attack, Block, Strength, Weak, Vulnerable, Dazed, Wound }

public enum EffectTarget { Self, Player }
public enum StatusPile { Draw, Discard }

public sealed record ActionEffect(EffectKind Kind, int Amount, int Hits = 1)
{
    public EffectTarget Target => Kind is EffectKind.Block or EffectKind.Strength
        ? EffectTarget.Self : EffectTarget.Player;
    public StatusPile? Pile => Kind switch
    {
        EffectKind.Dazed => StatusPile.Draw,
        EffectKind.Wound => StatusPile.Discard,
        _ => null
    };
}

public sealed record ActionContext(
    double ExpectedDamageAgainstMonster,
    double ExpectedPlayerAttackDamage,
    double ExpectedEnemyAttackDamageAgainstPlayer,
    int Strength = 0,
    bool Weakened = false,
    bool PlayerAlreadyWeak = false,
    bool PlayerAlreadyVulnerable = false,
    bool PlayerHasArtifact = false,
    IReadOnlyList<ForecastWindow>? RemainingForecast = null,
    double DazedDraws = 0,
    double WoundDraws = 0,
    double ExistingBlock = 0);

public sealed record ActionValuationSettings(
    double Block, double Weak, double WeakDamageReduction, double VulnerableDamageIncrease,
    double CardSlot, double Discount,
    int MaxAddedStrength, int MaxInsertedCards, int PlayerDebuffCooldownTurns,
    double MinimumAttackValueShare = 0.35);

public sealed record ActionHistory(
    int AddedStrength = 0, int InsertedCards = 0, int LastPlayerDebuffTurn = int.MinValue,
    int LastPureBlockTurn = int.MinValue)
{
    // Record only actions which the caller actually executed.
    public ActionHistory Record(ActionCandidate action, int turn)
    {
        var effects = action.Effects ?? [];
        return new ActionHistory(
            AddedStrength + effects.Where(effect => effect.Kind == EffectKind.Strength).Sum(effect => effect.Amount),
            InsertedCards + effects.Where(effect => effect.Kind is EffectKind.Dazed or EffectKind.Wound)
                .Sum(effect => effect.Amount),
            effects.Any(effect => effect.Kind is EffectKind.Weak or EffectKind.Vulnerable)
                ? turn : LastPlayerDebuffTurn,
            !action.IsAttack && effects.Any(effect => effect.Kind == EffectKind.Block)
                ? turn : LastPureBlockTurn);
    }
}

public sealed class ActionCatalog(ActionValuationSettings settings)
{
    public IEnumerable<ActionCandidate> Generate(
        MonsterProfile profile, ActionContext context, ActionHistory history, int turn)
    {
        var hits = (profile.HitWeights ?? new Dictionary<int, double> { [1] = 1 })
            .Where(pair => pair.Key > 0 && pair.Value > 0).OrderBy(pair => pair.Key).ToArray();
        var actions = profile.EnabledEffects ?? [];
        var survival = context.RemainingForecast ??
            SurvivalForecast.FromExpectedSurvival(profile.BaseExpectedSurvivalTurns, 8);
        double expectedHits = hits.Length == 0 ? 0 : hits.Sum(pair => pair.Key * pair.Value) / hits.Sum(pair => pair.Value);
        // The current action occupies this turn. Strength first changes a later attack.
        double futureWindowWeight = survival.Select((window, index) =>
            index == 0 ? 0 : window.AliveAtAction * Math.Pow(settings.Discount, index)).Sum();
        bool canDebuff = !context.PlayerHasArtifact &&
            (history.LastPlayerDebuffTurn == int.MinValue ||
                turn - history.LastPlayerDebuffTurn > settings.PlayerDebuffCooldownTurns);

        var extras = new List<(ActionEffect Effect, IntentStyle Style, double Value)>();
        foreach (var kind in actions.Distinct())
        {
            switch (kind)
            {
                case EffectKind.Block:
                    foreach (int amount in new[] { 3, 5, 8 })
                        Add(kind, amount, IntentStyle.Defend,
                            Math.Min(amount, Math.Max(0, context.ExpectedDamageAgainstMonster - context.ExistingBlock)) *
                            settings.Block);
                    break;
                case EffectKind.Strength when history.AddedStrength < settings.MaxAddedStrength:
                    Add(kind, 1, IntentStyle.Buff, expectedHits * profile.AttackFrequency * futureWindowWeight);
                    break;
                case EffectKind.Weak when canDebuff && !context.PlayerAlreadyWeak:
                    Add(kind, 1, IntentStyle.PlayerDebuff,
                        context.ExpectedPlayerAttackDamage * settings.WeakDamageReduction * settings.Weak);
                    break;
                case EffectKind.Vulnerable when canDebuff && !context.PlayerAlreadyVulnerable:
                    Add(kind, 1, IntentStyle.PlayerDebuff,
                        context.ExpectedEnemyAttackDamageAgainstPlayer * settings.VulnerableDamageIncrease);
                    break;
                case EffectKind.Dazed when history.InsertedCards < settings.MaxInsertedCards:
                    Add(kind, 1, IntentStyle.PlayerDebuff, settings.CardSlot * Math.Clamp(context.DazedDraws, 0, 1));
                    break;
                case EffectKind.Wound when history.InsertedCards < settings.MaxInsertedCards:
                    Add(kind, 1, IntentStyle.PlayerDebuff, settings.CardSlot * context.WoundDraws);
                    break;
            }
        }

        // Normalize alternatives within a style so adding another dose does not inflate its weight.
        var extraCounts = extras.GroupBy(extra => extra.Style).ToDictionary(group => group.Key, group => group.Count());
        foreach (var extra in extras)
        {
            if (extra.Effect.Kind == EffectKind.Block && history.LastPureBlockTurn == turn - 1)
                continue;
            yield return new ActionCandidate(
                $"{extra.Effect.Kind} {extra.Effect.Amount}", extra.Style, false, extra.Value, 0,
                1.0 / extraCounts[extra.Style], Effects: [extra.Effect]);
        }

        foreach (var hit in hits)
        {
            // Keep light attacks, while avoiding token hits that would distort the attack rate.
            int minPerHit = Math.Max(1, (int)Math.Ceiling(profile.ActionValue * settings.MinimumAttackValueShare / hit.Key));
            int maxPerHit = (int)Math.Ceiling(profile.ActionValue * (1 + profile.Volatility) / hit.Key);
            double weight = hit.Value / Math.Max(1, maxPerHit - minPerHit + 1);
            for (int amount = minPerHit; amount <= maxPerHit; amount++)
            {
                var attack = ActionCandidate.Attack($"{hit.Key}x{amount}", hit.Key, amount,
                    context.Strength, context.Weakened, weight);
                if (attack.Damage <= 0)
                    continue;

                yield return attack;
                foreach (var extra in extras)
                {
                    if (extra.Value > profile.ActionValue * 0.5 ||
                        attack.Value + extra.Value > profile.ActionValue * (1 + profile.Volatility))
                        continue;

                    yield return new ActionCandidate(
                        $"{attack.Id} + {extra.Effect.Kind} {extra.Effect.Amount}", extra.Style, true,
                        attack.Value + extra.Value, attack.Damage, attack.Weight / extraCounts[extra.Style],
                        attack.Damage + extra.Value, [attack.Effects![0], extra.Effect]);
                }
            }
        }

        void Add(EffectKind kind, int amount, IntentStyle style, double value)
        {
            if (value > 0 && profile.IntentWeights.GetValueOrDefault(style) > 0)
                extras.Add((new ActionEffect(kind, amount), style, value));
        }
    }
}
