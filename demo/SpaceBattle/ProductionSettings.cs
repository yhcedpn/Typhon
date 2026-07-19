using Typhon.Engine;

namespace SpaceBattle;

public static class SpaceBattleProductionSettings
{
    public const int AutomaticWorkerCount = -1;
    public const int DisabledQueueGrowthEscalationTicks = 0;

    public static SpaceBattleResourceEnvelope ResourceEnvelope { get; } = new(
        PageCacheSizeBytes: 512UL * 1024 * 1024,
        MemoryBudgetBytes: 1024UL * 1024 * 1024);

    public static string GetProfilerTracePath(string databaseLocation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseLocation);
        return Path.ChangeExtension(databaseLocation, ".typhon-trace");
    }
}

public sealed record SpaceBattleResourceEnvelope(
    ulong PageCacheSizeBytes,
    ulong MemoryBudgetBytes)
{
    public void Validate()
    {
        if (MemoryBudgetBytes == 0)
        {
            throw new InvalidOperationException("Typhon 内存封套必须大于零。");
        }

        if (PageCacheSizeBytes < PagedMMFOptions.MinimumCacheSizeBytes ||
            PageCacheSizeBytes % PagedMMFOptions.PageSizeBytes != 0)
        {
            throw new InvalidOperationException("Typhon page cache 大小必须符合引擎的页缓存约束。");
        }

        if (PageCacheSizeBytes > MemoryBudgetBytes)
        {
            throw new InvalidOperationException("Typhon page cache 不能超过总内存封套。");
        }
    }
}

public sealed record SpaceBattleRuntimeConfiguration(
    ulong PageCacheSizeBytes,
    ulong MemoryBudgetBytes,
    int ConfiguredWorkerCount,
    int EffectiveWorkerCount,
    int OverloadMinimumTickRateHz,
    int QueueGrowthEscalationTicks,
    OverloadLevel CurrentOverloadLevel,
    IReadOnlyList<SpaceBattleSystemConfiguration> Systems);

public sealed record SpaceBattleSystemConfiguration(
    string Name,
    SystemPriority Priority,
    int TickDivisor,
    int ThrottledTickDivisor,
    bool CanShed);
