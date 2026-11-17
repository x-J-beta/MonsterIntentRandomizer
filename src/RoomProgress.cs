namespace MonsterIntentRandomizer;

public sealed record PlayerActionCurve(double Base = 8, double RoomsPerStep = 3, double Exponent = 4.0 / 3,
    bool UseCombatDamage = false)
{
    public double At(int rooms, double? averageDamage = null)
    {
        double baseline = Math.Pow(Base + Math.Max(0, rooms) / RoomsPerStep, Exponent);
        return UseCombatDamage && averageDamage is double observed ? 0.9 * baseline + 0.1 * observed : baseline;
    }

    public double Survival(double hp, double allyHp, int rooms, double? averageDamage = null) =>
        (hp + 0.5 * allyHp) / At(rooms, averageDamage);

    internal MonsterProfile WithCombatEstimates(MonsterProfile profile, double hp, double allyHp, int rooms,
        double? averageDamage = null)
    {
        double playerAction = At(rooms, averageDamage);
        return profile with
        {
            BaseExpectedSurvivalTurns = (hp + 0.5 * allyHp) / playerAction,
            ExpectedCombatTurns = (hp + allyHp) / playerAction,
            PlayerActionValue = playerAction,
            CurrentHp = hp,
            RoomsEntered = rooms + 1
        };
    }
}

public sealed record RoomWeight(double Base = 1, double PerRoom = 0)
{
    internal const double DisabledBase = -100;
    internal bool IsDisabled => Base == DisabledBase;

    public double At(int rooms, double chaosProgress = 0)
    {
        if (IsDisabled)
            return 0;
        if (chaosProgress == 1)
            return 1;

        double exponent = 6 - 5 * Math.Clamp(PerRoom / 0.04, 0, 1);
        double baseWeight = Base < 1
            ? Base + (Math.Pow(2 - Base, Math.Pow(chaosProgress, exponent)) - 1)
            : Math.Pow(Base, 1 - chaosProgress);
        double perRoom = PerRoom * (1 - chaosProgress * chaosProgress);
        return Math.Max(0, baseWeight + Math.Max(0, rooms) * perRoom);
    }
}
