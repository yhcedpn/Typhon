namespace SpaceBattle;

internal struct SplitMix64
{
    private const ulong Increment = 0x9E37_79B9_7F4A_7C15UL;
    private const ulong MultiplierA = 0xBF58_476D_1CE4_E5B9UL;
    private const ulong MultiplierB = 0x94D0_49BB_1331_11EBUL;
    private ulong _state;

    public SplitMix64(ulong seed)
    {
        _state = seed;
    }

    public ulong NextUInt64()
    {
        return Mix(unchecked(_state += Increment));
    }
    internal static ulong Mix(ulong value)
    {
        value = unchecked((value ^ (value >> 30)) * MultiplierA);
        value = unchecked((value ^ (value >> 27)) * MultiplierB);
        return value ^ (value >> 31);
    }


    public float NextUnitFloat()
    {
        return (NextUInt64() >> 40) * (1f / 16_777_216f);
    }

    public float NextCoordinate(float exclusiveUpperBound)
    {
        return NextUnitFloat() * exclusiveUpperBound;
    }
}
