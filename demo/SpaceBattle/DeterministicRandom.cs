namespace SpaceBattle;

internal static class DeterministicRandom
{
    private const float UnitFloatScale = 1f / 16_777_216f;

    public static float Coordinate(
        ulong seed,
        ulong entityId,
        ulong decisionOrdinal,
        ulong purpose,
        float worldSize)
    {
        var value = Mix(seed ^ Mix(entityId) ^ Mix(decisionOrdinal) ^ Mix(purpose));
        var unit = (value >> 40) * UnitFloatScale;
        return unit * worldSize;
    }

    private static ulong Mix(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}