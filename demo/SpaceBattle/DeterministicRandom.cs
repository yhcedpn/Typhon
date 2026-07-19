namespace SpaceBattle;

internal static class DeterministicRandom
{
    private const float UnitFloatScale = 1f / 16_777_216f;
    private const float TwoPi = 2f * MathF.PI;

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

    public static float UnitInterval(
        ulong seed,
        ulong entityId,
        ulong decisionOrdinal,
        ulong purpose)
    {
        var value = Mix(seed ^ Mix(entityId) ^ Mix(decisionOrdinal) ^ Mix(purpose));
        return (value >> 40) * UnitFloatScale;
    }

    public static MotionSnapshot UnitDirection(
        ulong seed,
        ulong entityId,
        ulong decisionOrdinal,
        ulong azimuthPurpose,
        ulong elevationPurpose)
    {
        var azimuth = UnitInterval(seed, entityId, decisionOrdinal, azimuthPurpose) * TwoPi;
        var z = (UnitInterval(seed, entityId, decisionOrdinal, elevationPurpose) * 2f) - 1f;
        var radial = MathF.Sqrt(MathF.Max(0f, 1f - (z * z)));
        return new MotionSnapshot(
            radial * MathF.Cos(azimuth),
            radial * MathF.Sin(azimuth),
            z,
            0f);
    }

    private static ulong Mix(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}
