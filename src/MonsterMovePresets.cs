namespace MonsterIntentRandomizer;

internal sealed record MonsterMovePresets(Dictionary<string, string[]> Moves,
    Dictionary<string, SimulationMoveSequence> Sequences)
{
    internal string[] MoveIds(string id) => Moves.GetValueOrDefault(id) ?? [];

    internal SimulationMoveSequence? SequenceFor(string id, string? phaseId = null)
    {
        if (Sequences.TryGetValue(id, out var sequence))
            return phaseId == null ? sequence : sequence.Phases?.GetValueOrDefault(phaseId) ?? sequence;
        var moves = MoveIds(id);
        return moves.Length == 0 ? null : new SimulationMoveSequence(moves);
    }
}

internal sealed record SimulationMoveSequence(string[] Loop, string[]? Opening = null, int RepeatOpening = 1,
    Dictionary<string, SimulationMoveSequence>? Phases = null)
{
    internal string First => At(0);

    internal string At(int index)
    {
        int openingCount = (Opening?.Length ?? 0) * RepeatOpening;
        return index < openingCount ? Opening![index % Opening.Length] : Loop[(index - openingCount) % Loop.Length];
    }
}

internal static class MoveRandom
{
    internal static Random For(ulong seed, int floor, uint creatureId, int round)
    {
        unchecked
        {
            uint mixed = (uint)seed ^ (uint)(seed >> 32);
            mixed = (mixed ^ (uint)floor) * 16777619;
            mixed = (mixed ^ creatureId) * 16777619;
            mixed = (mixed ^ (uint)round) * 16777619;
            return new Random((int)mixed);
        }
    }
}
