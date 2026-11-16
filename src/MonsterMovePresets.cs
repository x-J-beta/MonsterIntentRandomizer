namespace MonsterIntentRandomizer;

internal sealed record MonsterMovePresets(Dictionary<string, string[]> Moves,
    Dictionary<string, SimulationMoveSequence> Sequences, Dictionary<string, string>? Aliases = null)
{
    internal string ProfileId(string id) => Aliases?.GetValueOrDefault(id) ?? id;

    internal string[] MoveIds(string id) => Moves.GetValueOrDefault(ProfileId(id)) ?? [];

    internal SimulationMoveSequence? SequenceFor(string id, string? phaseId = null)
    {
        if (Sequences.TryGetValue(ProfileId(id), out var sequence))
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

internal static class CombatRandom
{
    internal static Random For(ulong seed, int floor)
    {
        unchecked
        {
            const uint mix = 16777619;
            uint mixed = (uint)seed ^ (uint)(seed >> 32);
            mixed = (mixed ^ (uint)floor) * mix;
            // Preserve the previous opening seed, which used zero for the creature and round.
            return new Random((int)(mixed * mix * mix));
        }
    }
}
