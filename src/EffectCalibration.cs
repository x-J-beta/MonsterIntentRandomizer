using System.Text.Json;

namespace MonsterIntentRandomizer;

internal static class EffectCalibration
{
    private sealed record Scenario(string Id, int Rooms, int Hp, int AllyHp, int Attack, int Hits, int Deck);
    private sealed record Probe(string Id, EffectKind Kind, int Amount, StatusPile? Pile = null, bool Opening = false);
    private readonly record struct Card(int Damage = 0, int Hits = 1, int Block = 0, int Draw = 0,
        EffectKind Status = EffectKind.Wait, bool Exposed = false, bool Debuff = false);
    private sealed record Outcome(double Lost, double EnemyHp, int Rounds, int Draws, int Triggers, int AppliedAmount)
    {
        public double Pressure => Lost + EnemyHp * 0.2;
    }

    private static readonly Scenario[] Scenarios =
    [
        new("early-single", 4, 56, 0, 14, 1, 0),
        new("early-pair", 8, 54, 30, 13, 1, 2),
        new("early-elite", 6, 114, 0, 18, 1, 2),
        new("mid-multihit", 17, 92, 28, 20, 2, 1),
        new("mid-guard", 22, 120, 36, 23, 1, 2),
        new("late-single", 30, 178, 0, 30, 1, 0),
        new("late-multihit", 35, 210, 45, 32, 3, 1),
        new("late-boss", 36, 405, 0, 39, 2, 0)
    ];

    private static readonly Probe[] PowerProbes =
    [
        new("Block", EffectKind.Block, 8), new("Heal", EffectKind.Heal, 8),
        new("Strength", EffectKind.Strength, 3), new("Ritual", EffectKind.Ritual, 1),
        new("Regen", EffectKind.Regen, 5), new("Plating", EffectKind.Plating, 6),
        new("HardenedShell", EffectKind.HardenedShell, -1), new("HardToKill", EffectKind.HardToKill, -1),
        new("HardenedShell-half", EffectKind.HardenedShell, -2), new("HardToKill-half", EffectKind.HardToKill, -2),
        new("Intangible", EffectKind.Intangible, 1), new("Artifact", EffectKind.Artifact, 1),
        new("Barricade", EffectKind.Barricade, 1), new("CurlUp", EffectKind.CurlUp, 8),
        new("Thorns", EffectKind.Thorns, 2), new("TemporaryThorns", EffectKind.TemporaryThorns, 3),
        new("Weak", EffectKind.Weak, 1), new("Vulnerable", EffectKind.Vulnerable, 1), new("Frail", EffectKind.Frail, 2),
        new("StrengthDown", EffectKind.StrengthDown, 2), new("DexterityDown", EffectKind.DexterityDown, 2),
        new("Tangled", EffectKind.Tangled, 1), new("Ringing", EffectKind.Ringing, 1), new("Smoggy", EffectKind.Smoggy, 1),
        new("Tender", EffectKind.Tender, 1), new("Poison", EffectKind.Poison, 2), new("Shrink", EffectKind.Shrink, 1),
        new("Soulbind", EffectKind.Soulbind, 2), new("DrawDown", EffectKind.DrawDown, 1), new("EnergyDown", EffectKind.EnergyDown, 1),
        new("PersonalHive", EffectKind.PersonalHive, 1), new("VitalSpark", EffectKind.VitalSpark, 1),
        new("WitheringPresence", EffectKind.WitheringPresence, 1),
        new("Envenom", EffectKind.Envenom, 1), new("PaperCuts", EffectKind.PaperCuts, 1)
    ];

    internal static void Run(RuleConfiguration rules, int seeds)
    {
        var cards = Enum.GetValues<EffectKind>().Where(kind => ActionEffect.IsCard(kind) &&
            rules.Actions.Weights?.GetValueOrDefault(kind)?.Base != -1);
        var probes = PowerProbes.Where(probe => rules.Actions.Weights?.GetValueOrDefault(probe.Kind)?.Base != -1)
            .Concat(cards.SelectMany(kind => new[] { StatusPile.Top, StatusPile.Draw, StatusPile.Discard }.Select(pile =>
                new Probe($"{kind}@{pile}", kind, rules.Actions.MinimumAmounts?.GetValueOrDefault(kind, 1) ?? 1, pile))))
            .Concat(PowerProbes.Where(probe => probe.Kind is EffectKind.Strength or EffectKind.Ritual or
                EffectKind.Regen or EffectKind.Plating or EffectKind.HardenedShell or EffectKind.HardToKill or
                EffectKind.Artifact or EffectKind.Thorns or EffectKind.TemporaryThorns or EffectKind.Intangible or
                EffectKind.VitalSpark or EffectKind.WitheringPresence or
                EffectKind.PersonalHive or EffectKind.Envenom or EffectKind.PaperCuts or EffectKind.Barricade or EffectKind.CurlUp)
                .Select(probe => probe with { Id = "Open/" + probe.Id, Opening = true }))
            .Concat(new Probe[]
            {
                new("Open/Weak", EffectKind.Weak, 2, Opening: true),
                new("Open/Vulnerable", EffectKind.Vulnerable, 2, Opening: true),
                new("Open/StrengthDown", EffectKind.StrengthDown, 2, Opening: true),
                new("Open/Demise", EffectKind.Heal, -2, Opening: true),
                new("Open/Doom", EffectKind.Block, -8, Opening: true),
                new("Open/Stun", EffectKind.Wait, 1, Opening: true),
                new("Open/Imbalanced", EffectKind.Wait, 2, Opening: true)
            }).ToArray();

        var trials = new List<object>();
        foreach (var scenario in Scenarios)
        {
            double playerAction = rules.PlayerAction.At(scenario.Rooms);
            var bases = Enumerable.Range(0, seeds)
                .Select(seed => Fight(scenario, playerAction, rules.PlayerAction, seed, null)).ToArray();
            var reference = Enumerable.Range(0, seeds)
                .Select(seed => Fight(scenario, playerAction, rules.PlayerAction, seed, new("Reference", EffectKind.Attack, 6))).ToArray();
            var openingReference = Enumerable.Range(0, seeds)
                .Select(seed => Fight(scenario, playerAction, rules.PlayerAction, seed,
                    new("Opening reference", EffectKind.Attack, 6, Opening: true))).ToArray();
            foreach (var probe in probes)
            {
                var paired = Enumerable.Range(0, seeds).Select(seed =>
                    (Baseline: bases[seed], With: Fight(scenario, playerAction, rules.PlayerAction, seed, probe),
                        Reference: probe.Opening ? openingReference[seed] : reference[seed])).Where(pair => pair.With.AppliedAmount != 0).ToArray();
                if (paired.Length == 0)
                    continue;
                double pressure = paired.Average(pair => pair.With.Pressure - pair.Baseline.Pressure);
                double referencePressure = paired.Average(pair => pair.Reference.Pressure - pair.Baseline.Pressure);
                trials.Add(new
                {
                    scenario = scenario.Id, roomsEntered = scenario.Rooms + 1, effect = probe.Id,
                    averageAmount = Round(paired.Average(pair => Math.Abs(pair.With.AppliedAmount))),
                    observed = paired.Length, pressureDelta = Round(pressure),
                    equivalentActionValue = Round(6 * pressure / Math.Max(referencePressure, 0.5)),
                    playerHpLostDelta = Round(paired.Average(pair => pair.With.Lost - pair.Baseline.Lost)),
                    roundsDelta = Round(paired.Average(pair => pair.With.Rounds - pair.Baseline.Rounds)),
                    draws = Round(paired.Average(pair => pair.With.Draws)),
                    triggers = Round(paired.Average(pair => pair.With.Triggers))
                });
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            model = "Paired offline combat with synthetic decks, hand/draw/discard, energy, block, HP and persistent effects. Reference is six additional advertised damage. Stage profiles and rare powers are approximations.",
            seedsPerScenario = seeds,
            scenarios = Scenarios,
            trials,
            gaps = new[] { "Player archetypes use synthetic card lists", "The enemy pressure schedule is held constant across each pair", "Cross-status interactions are sampled through three deck archetypes and baseline guard intents" }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static double Round(double value) => Math.Round(value, 4);

    private static Outcome Fight(Scenario scenario, double playerAction, PlayerActionCurve curve, int seed, Probe? probe)
    {
        var shuffle = new Random(seed * 7919 + 17041);
        var insert = new Random(seed * 4889 + 6421);
        var enemyRolls = new Random(seed * 3121 + 8101);
        var drawPile = BuildDeck(scenario);
        Shuffle(drawPile, shuffle);
        var discard = new List<Card>();
        var hand = new List<Card>(9);
        int mainHp = scenario.Hp, allyHp = scenario.AllyHp, playerHp = 78;
        int enemyBlock = 0, playerBlock = 0, enemyStrength = 0, playerStrengthLoss = 0, dexterityLoss = 0;
        int regen = 0, plating = 0, ritual = 0, shell = 0, hardToKill = 0, thorns = 0, temporaryThorns = 0;
        int enemyWeak = 0, enemyVulnerable = 0, intangible = 0, artifact = 0, curlUp = 0, stun = 0;
        int playerWeak = 0, playerVulnerable = 0, frail = 0, tangled = 0, ringing = 0, poison = 0;
        int soulbind = 0, drawDown = 0, energyDown = 0, hive = 0, sparks = 0, withering = 0, envenom = 0, paperCuts = 0;
        int demise = 0, doom = 0, appliedRound = 0, drawn = 0, triggered = 0, totalPlayed = 0, round = 0;
        bool barricade = false, smoggy = false, shrink = false, tender = false, imbalanced = false;
        int appliedAmount = 0;
        var scale = playerAction / 16.0;

        if (probe?.Opening == true)
            Apply(probe);

        for (round = 1; round <= 18 && playerHp > 0 && (mainHp > 0 || allyHp > 0); round++)
        {
            bool mainActs = mainHp > 0 && stun == 0;
            bool attacksOnThisRound = enemyRolls.NextDouble() < 0.72;
            bool attack = mainActs && attacksOnThisRound;
            int hit = attack ? (int)Math.Floor(Math.Max(0, scenario.Attack / (double)scenario.Hits + enemyStrength) *
                (enemyWeak > 0 ? 0.75 : 1) * (playerVulnerable > 0 ? 1.5 : 1)) : 0;
            int referenceHit = mainHp > 0 && probe?.Kind == EffectKind.Attack && round == (probe.Opening ? 1 : 2)
                ? probe.Amount : 0;
            int allyHit = allyHp > 0 ? Math.Max(3, scenario.Attack / 3) : 0;
            int incoming = hit * scenario.Hits + allyHit + referenceHit;
            playerBlock = 0;
            playerHp -= poison;
            poison = Math.Max(0, poison - 1);
            int energy = Math.Max(0, 3 - energyDown);
            int played = 0, skills = 0, bound = soulbind, lostThisRound = 0, tainted = 0;
            Draw(Math.Max(0, 5 - drawDown));
            while (playerHp > 0 && (mainHp > 0 || allyHp > 0) && (ringing == 0 || played < 1))
            {
                int best = -1;
                double bestUtility = 0;
                for (int i = 0; i < hand.Count; i++)
                {
                    var card = hand[i];
                    bool isAttack = card.Damage > 0;
                    bool isSkill = !isAttack && (card.Block > 0 || card.Draw > 0 || card.Debuff || card.Status == EffectKind.Slimed);
                    if (card.Status is EffectKind.Debris or EffectKind.Toxic or EffectKind.Beckon)
                    {
                        if (energy > 1 && card.Status is EffectKind.Toxic or EffectKind.Beckon && bestUtility < 1.5)
                            (best, bestUtility) = (i, 1.5);
                        continue;
                    }
                    if ((!isAttack && !isSkill) || (smoggy && isSkill && skills > 0) ||
                        energy < (isAttack && tangled > 0 ? 2 : 1))
                        continue;
                    int defense = DefenseAmount(card);
                    int damage = AttackAmount(card) * card.Hits;
                    int draw = card.Status == EffectKind.Slimed ? 1 : card.Draw;
                    double utility = Math.Min(damage, Math.Max(0, mainHp + enemyBlock + allyHp)) +
                        Math.Min(defense, Math.Max(0, incoming - playerBlock)) * (scenario.Deck == 2 ? 1.55 : 1.15) +
                        (draw > 0 && energy > 1 ? 4 * scale : 0) + (card.Debuff && mainHp > 0 ? 2.5 * scale : 0);
                    if (card.Damage == 0 && draw > 0 && energy == 1)
                        utility -= 1;
                    if (isAttack && mainHp > 0 && damage >= mainHp + enemyBlock && shell == 0 && hardToKill == 0)
                        utility += 20;
                    if (utility > bestUtility)
                        (best, bestUtility) = (i, utility);
                }

                if (best < 0 || energy <= 0)
                    break;
                var chosen = hand[best];
                hand.RemoveAt(best);
                bool attackCard = chosen.Damage > 0;
                energy -= attackCard && tangled > 0 ? 2 : 1;
                if (!attackCard)
                {
                    skills++;
                    if (mainHp > 0 && (chosen.Block > 0 || chosen.Draw > 0))
                        tainted += sparks;
                }
                if (chosen.Status is not (EffectKind.Beckon or EffectKind.Toxic or EffectKind.Slimed or EffectKind.Debris))
                    discard.Add(chosen);
                if (chosen.Debuff && mainHp > 0)
                {
                    if (artifact > 0)
                        artifact--;
                    else
                        enemyVulnerable += 2;
                }

                playerBlock += DefenseAmount(chosen);
                int perHit = AttackAmount(chosen);
                bool wasHit = false;
                for (int strike = 0; strike < chosen.Hits && perHit > 0 && playerHp > 0; strike++)
                {
                    if (mainHp <= 0)
                    {
                        allyHp = Math.Max(0, allyHp - perHit);
                        continue;
                    }
                    int damage = hardToKill > 0 ? Math.Min(perHit, hardToKill) : perHit;
                    int healthLoss = Math.Max(0, damage - enemyBlock);
                    enemyBlock = Math.Max(0, enemyBlock - damage);
                    if (intangible > 0)
                        healthLoss = Math.Min(1, healthLoss);
                    if (shell > 0)
                        healthLoss = Math.Min(healthLoss, Math.Max(0, shell - lostThisRound));
                    mainHp = Math.Max(0, mainHp - healthLoss);
                    lostThisRound += healthLoss;
                    wasHit |= healthLoss > 0;
                    if (hive > 0)
                        Put(new Card(Status: EffectKind.Dazed), StatusPile.Draw);
                    if (thorns + temporaryThorns > 0)
                        DamagePlayer(thorns + temporaryThorns);
                }
                if (wasHit && curlUp > 0 && mainHp > 0)
                {
                    enemyBlock += curlUp;
                    curlUp = 0;
                }
                totalPlayed++;
                played++;
                if (withering > 0 && totalPlayed % 6 == 0)
                    hand.Add(new Card(Status: EffectKind.Wither));
                Draw(chosen.Status == EffectKind.Slimed ? 1 : chosen.Draw);
            }

            foreach (var card in hand.ToArray())
            {
                int loss = card.Status switch
                {
                    EffectKind.Burn => 2, EffectKind.Wither or EffectKind.Infection => 3,
                    EffectKind.Beckon => 6, EffectKind.Toxic => 5, _ => 0
                };
                if (loss > 0)
                {
                    if (card.Exposed)
                        triggered++;
                    if (card.Status == EffectKind.Beckon)
                        playerHp -= loss;
                    else
                        DamagePlayer(loss);
                }
                if (card.Status != EffectKind.Sadness)
                {
                    hand.Remove(card);
                    if (card.Status is not (EffectKind.Dazed or EffectKind.Void))
                        discard.Add(card);
                }
            }

            if (doom > 0 && mainHp <= doom)
                mainHp = 0;
            temporaryThorns = 0;
            if (mainHp > 0 && playerHp > 0)
            {
                if (!barricade)
                    enemyBlock = 0;
                if (plating > 0)
                    plating--;
                if (round == 2 && probe is { Opening: false })
                    Apply(probe);
                if (attack)
                {
                    DamagePlayer(referenceHit);
                    int unblocked = 0;
                    for (int i = 0; i < scenario.Hits && playerHp > 0; i++)
                    {
                        int lost = DamagePlayer(hit + tainted);
                        unblocked += lost;
                        if (lost > 0)
                            poison += envenom;
                        if (lost > 0)
                            playerHp -= paperCuts;
                    }
                    if (imbalanced && unblocked == 0)
                        stun = 2;
                }
                else
                {
                    DamagePlayer(referenceHit);
                    if (mainActs)
                        enemyBlock += Math.Max(3, scenario.Attack / 4);
                }
                mainHp = Math.Min(scenario.Hp, mainHp + regen) - demise;
                enemyBlock += plating;
                regen = Math.Max(0, regen - 1);
                if (ritual > 0 && round > appliedRound)
                    enemyStrength += ritual;
                stun = Math.Max(0, stun - 1);
                enemyWeak = Math.Max(0, enemyWeak - 1);
                enemyVulnerable = Math.Max(0, enemyVulnerable - 1);
                if (appliedRound != round)
                    intangible = Math.Max(0, intangible - 1);
            }
            if (allyHp > 0)
                DamagePlayer(allyHit + tainted);
            if (appliedRound != round)
            {
                playerWeak = Math.Max(0, playerWeak - 1);
                playerVulnerable = Math.Max(0, playerVulnerable - 1);
                frail = Math.Max(0, frail - 1);
                tangled = Math.Max(0, tangled - 1);
                ringing = Math.Max(0, ringing - 1);
            }

            int AttackAmount(Card card) => card.Damage <= 0 ? 0 : (int)Math.Floor(Math.Max(0,
                Math.Round(card.Damage * scale) - playerStrengthLoss - (tender ? played : 0)) *
                (playerWeak > 0 ? 0.75 : 1) * (shrink && mainHp > 0 ? 0.7 : 1) * (enemyVulnerable > 0 && mainHp > 0 ? 1.5 : 1));
            int DefenseAmount(Card card) => card.Block <= 0 ? 0 : (int)Math.Floor(Math.Max(0,
                Math.Round(card.Block * Math.Sqrt(scale)) - dexterityLoss - (tender ? played : 0)) * (frail > 0 ? 0.75 : 1));
            void Draw(int count)
            {
                for (int i = 0; i < count; i++)
                {
                    if (drawPile.Count == 0)
                    {
                        drawPile.AddRange(discard);
                        discard.Clear();
                        Shuffle(drawPile, shuffle);
                    }
                    if (drawPile.Count == 0)
                        return;
                    var card = drawPile[^1];
                    drawPile.RemoveAt(drawPile.Count - 1);
                    if (card.Exposed)
                        drawn++;
                    if (card.Status == EffectKind.Void)
                    {
                        energy = Math.Max(0, energy - 1);
                        if (card.Exposed)
                            triggered++;
                    }
                    if (bound > 0)
                    {
                        bound--;
                        card = new Card(Status: EffectKind.Wound);
                    }
                    hand.Add(card);
                }
            }
        }
        return new Outcome(78 - Math.Max(0, playerHp), Math.Max(0, mainHp) + allyHp, round - 1, drawn, triggered, appliedAmount);

        int DamagePlayer(int damage)
        {
            int lost = Math.Max(0, damage - playerBlock);
            playerBlock = Math.Max(0, playerBlock - damage);
            playerHp -= lost;
            return lost;
        }

        void Apply(Probe effect)
        {
            int amount = effect.Amount;
            if (effect.Kind is EffectKind.HardenedShell or EffectKind.HardToKill && amount < 0)
            {
                double hpPerTurn = mainHp / curve.Survival(mainHp, allyHp, scenario.Rooms);
                double factor = effect.Kind == EffectKind.HardenedShell ? 1 : 0.5;
                amount = Math.Max(1, (int)Math.Floor(hpPerTurn * factor / -amount));
            }
            appliedAmount = amount;
            appliedRound = round;
            if (effect.Pile is StatusPile pile)
            {
                for (int i = 0; i < amount; i++)
                    Put(new Card(Status: effect.Kind, Exposed: true), pile);
                return;
            }
            switch (effect.Kind)
            {
                case EffectKind.Block when amount < 0: doom = -amount; break;
                case EffectKind.Heal when amount < 0: demise = -amount; break;
                case EffectKind.Block: enemyBlock += amount; break;
                case EffectKind.Heal: mainHp = Math.Min(scenario.Hp, mainHp + amount); break;
                case EffectKind.Strength: enemyStrength += amount; break;
                case EffectKind.Ritual: ritual += amount; break;
                case EffectKind.Regen: regen += amount; break;
                case EffectKind.Plating: plating += amount; break;
                case EffectKind.HardenedShell: shell = amount; break;
                case EffectKind.HardToKill: hardToKill = amount; break;
                case EffectKind.Intangible: intangible = amount; break;
                case EffectKind.Artifact: artifact += amount; break;
                case EffectKind.Barricade: barricade = true; break;
                case EffectKind.CurlUp: curlUp = amount; break;
                case EffectKind.Thorns: thorns += amount; break;
                case EffectKind.TemporaryThorns: temporaryThorns += amount; break;
                case EffectKind.Weak when effect.Opening: enemyWeak += amount; break;
                case EffectKind.Vulnerable when effect.Opening: enemyVulnerable += amount; break;
                case EffectKind.StrengthDown when effect.Opening: enemyStrength -= amount; break;
                case EffectKind.Weak: playerWeak += amount; break;
                case EffectKind.Vulnerable: playerVulnerable += amount; break;
                case EffectKind.Frail: frail += amount; break;
                case EffectKind.Tangled: tangled += amount; break;
                case EffectKind.Ringing: ringing = 1; break;
                case EffectKind.Smoggy: smoggy = true; break;
                case EffectKind.Tender: tender = true; break;
                case EffectKind.Poison: poison += amount; break;
                case EffectKind.Shrink: shrink = true; break;
                case EffectKind.Soulbind: soulbind += amount; break;
                case EffectKind.DrawDown: drawDown += amount; break;
                case EffectKind.EnergyDown: energyDown += amount; break;
                case EffectKind.StrengthDown: playerStrengthLoss += amount; break;
                case EffectKind.DexterityDown: dexterityLoss += amount; break;
                case EffectKind.PersonalHive: hive = amount; break;
                case EffectKind.VitalSpark: sparks += amount; break;
                case EffectKind.WitheringPresence: withering = amount; break;
                case EffectKind.Envenom: envenom = amount; break;
                case EffectKind.PaperCuts: paperCuts = amount; break;
                case EffectKind.Wait when amount == 1: stun = 1; break;
                case EffectKind.Wait: imbalanced = true; break;
            }
        }

        void Put(Card card, StatusPile pile)
        {
            if (pile == StatusPile.Discard)
                discard.Add(card);
            else if (pile == StatusPile.Top)
                drawPile.Add(card);
            else
                drawPile.Insert(insert.Next(drawPile.Count + 1), card);
        }
    }

    private static List<Card> BuildDeck(Scenario scenario)
    {
        var deck = new List<Card>();
        for (int i = 0; i < 10; i++)
            deck.Add(scenario.Deck == 1 && i % 3 != 0 ? new Card(3 + i % 2, 2 + i % 2) : new Card(6 + i % 4));
        for (int i = 0; i < (scenario.Deck == 2 ? 10 : 7); i++)
            deck.Add(new Card(Block: 5 + i % 4));
        deck.AddRange([new Card(Block: 4, Draw: 1), new Card(Draw: 2), new Card(Damage: 4, Debuff: true)]);
        return deck;
    }

    private static void Shuffle(List<Card> cards, Random random)
    {
        for (int i = cards.Count - 1; i > 0; i--)
        {
            int next = random.Next(i + 1);
            (cards[i], cards[next]) = (cards[next], cards[i]);
        }
    }
}
