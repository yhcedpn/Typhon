namespace SpaceBattle;

public sealed record SimulationDefinition
{
    public const int DefaultShipCount = 50_000;
    public const ulong DefaultSeed = 0x5350_4143_4542_4154UL;
    public const float DefaultWorldWidth = 1_000f;
    public const float DefaultWorldHeight = 1_000f;
    public const float DefaultWorldDepth = 400f;
    public const uint DefaultMaximumHealth = 1_000;
    public const int FixedTickRate = 25;
    public const float FixedSimulationDeltaSeconds = 0.04f;
    public const ulong DefaultMaximumCompletedTicks = 22_500;
    public const float DefaultSpatialCellSize = 100f;

    public static SimulationDefinition Default { get; } = new();

    public SimulationDefinition()
    {
        RunName = "default";
        ShipCount = DefaultShipCount;
        Seed = DefaultSeed;
        RulesetVersion = 1;
        WorldWidth = DefaultWorldWidth;
        WorldHeight = DefaultWorldHeight;
        WorldDepth = DefaultWorldDepth;
        MaximumHealth = DefaultMaximumHealth;
        TickRate = FixedTickRate;
        FixedDeltaSeconds = FixedSimulationDeltaSeconds;
        MaximumCompletedTicks = DefaultMaximumCompletedTicks;
        SpatialCellSize = DefaultSpatialCellSize;
    }

    public SimulationDefinition(
        int shipCount,
        ulong seed = DefaultSeed,
        float worldWidth = DefaultWorldWidth,
        float worldHeight = DefaultWorldHeight,
        float worldDepth = DefaultWorldDepth,
        uint maximumHealth = DefaultMaximumHealth,
        int tickRate = FixedTickRate,
        float fixedDeltaSeconds = FixedSimulationDeltaSeconds,
        ulong maximumCompletedTicks = DefaultMaximumCompletedTicks,
        float spatialCellSize = DefaultSpatialCellSize,
        string runName = "test",
        uint rulesetVersion = 1)
    {
        RunName = runName;
        ShipCount = shipCount;
        Seed = seed;
        RulesetVersion = rulesetVersion;
        WorldWidth = worldWidth;
        WorldHeight = worldHeight;
        WorldDepth = worldDepth;
        MaximumHealth = maximumHealth;
        TickRate = tickRate;
        FixedDeltaSeconds = fixedDeltaSeconds;
        MaximumCompletedTicks = maximumCompletedTicks;
        SpatialCellSize = spatialCellSize;
    }

    public string RunName { get; init; }
    public int ShipCount { get; init; }
    public ulong Seed { get; init; }
    public uint RulesetVersion { get; init; }
    public float WorldWidth { get; init; }
    public float WorldHeight { get; init; }
    public float WorldDepth { get; init; }
    public uint MaximumHealth { get; init; }
    public int TickRate { get; init; }
    public float FixedDeltaSeconds { get; init; }
    public ulong MaximumCompletedTicks { get; init; }
    public float SpatialCellSize { get; init; }

    public float WorldSizeX => WorldWidth;
    public float WorldSizeY => WorldHeight;
    public float WorldSizeZ => WorldDepth;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RunName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ShipCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(WorldWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(WorldHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(WorldDepth);
        ArgumentOutOfRangeException.ThrowIfZero(MaximumHealth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TickRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(FixedDeltaSeconds);
        ArgumentOutOfRangeException.ThrowIfZero(MaximumCompletedTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SpatialCellSize);

        if (!float.IsFinite(WorldWidth) || !float.IsFinite(WorldHeight) || !float.IsFinite(WorldDepth) ||
            !float.IsFinite(FixedDeltaSeconds) || !float.IsFinite(SpatialCellSize))
        {
            throw new ArgumentException("模拟配置中的浮点值必须有限。", nameof(SimulationDefinition));
        }
    }
}
