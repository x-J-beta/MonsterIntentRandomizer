namespace MonsterIntentRandomizer;

public sealed record PlayerActionCurve(double Base = 8, double RoomsPerStep = 3, double Exponent = 4.0 / 3)
{
    public double At(int rooms) => Math.Pow(Base + Math.Max(0, rooms) / RoomsPerStep, Exponent);

    public double Survival(double hp, double allyHp, int rooms) => (hp + 0.5 * allyHp) / At(rooms);
}

public sealed record RoomWeight(double Base = 1, double PerRoom = 0)
{
    public double At(int rooms) => Math.Max(0, Base + Math.Max(0, rooms) * PerRoom);
}
