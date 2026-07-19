namespace SpaceBattle;

public sealed record SimulationDefinition
{
    public const ulong DefaultSeed = 0x5350414345424154UL;

    public static SimulationDefinition Default { get; } = new(
        runName: "default",
        shipCount: 50_000,
        seed: DefaultSeed,
        rulesetVersion: 1,
        worldSize: 1_000f,
        maximumHealth: 1_000,
        stagingTicks: 250,
        spatialCellSize: 100f,
        spatialMargin: 20f);

    public SimulationDefinition(
        string runName,
        int shipCount,
        ulong seed,
        uint rulesetVersion,
        float worldSize,
        uint maximumHealth,
        ushort stagingTicks,
        float spatialCellSize,
        float spatialMargin)
    {
        RunName = runName;
        ShipCount = shipCount;
        Seed = seed;
        RulesetVersion = rulesetVersion;
        WorldSize = worldSize;
        MaximumHealth = maximumHealth;
        StagingTicks = stagingTicks;
        SpatialCellSize = spatialCellSize;
        SpatialMargin = spatialMargin;
    }

    public string RunName { get; }
    public int ShipCount { get; }
    public ulong Seed { get; }
    public uint RulesetVersion { get; }
    public float WorldSize { get; }
    public uint MaximumHealth { get; }
    public ushort StagingTicks { get; }
    public float SpatialCellSize { get; }
    public float SpatialMargin { get; }
}