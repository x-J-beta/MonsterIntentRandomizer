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
}

public sealed record RoomWeight(double Base = 1, double PerRoom = 0)
{
    internal const double DisabledBase = -100;
    internal bool IsDisabled => Base == DisabledBase;

    public double At(int rooms, double chaosProgress = 0)
    {
        if (IsDisabled)
            return 0;
        double baseWeight = (1 - chaosProgress) * Base + chaosProgress;
        if (Base < 0 && PerRoom > 0)
        {
            double exponent = 5 - 3 * Math.Clamp(PerRoom / 0.05, 0, 1);
            baseWeight = Base * (1 - Math.Pow(chaosProgress, exponent));
        }
        return Math.Max(0, baseWeight + Math.Max(0, rooms) * PerRoom);
    }
}
