using System.Globalization;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle.Tests;

// 临时复现测试：长跑多 worker 场景下 damage/deaths 是否停滞（真实 main 22,500 tick 观测到停滞）。
[TestFixture]
public sealed class StallReproTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "SpaceBattle.Tests", TestContext.CurrentContext.Test.ID);
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    [Explicit("临时诊断探针：复现伤害结算停滞；Release-only，勿纳入常规回归。")]
    public void LongRun_MultiWorker_DamageStallProbe()
    {
        var definition = new SimulationDefinition(
            shipCount: 5_000,
            seed: SimulationDefinition.DefaultSeed,
            worldWidth: SimulationDefinition.DefaultWorldWidth,
            worldHeight: SimulationDefinition.DefaultWorldHeight,
            worldDepth: SimulationDefinition.DefaultWorldDepth,
            maximumHealth: SimulationDefinition.DefaultMaximumHealth,
            tickRate: 1_000,
            fixedDeltaSeconds: SimulationDefinition.FixedSimulationDeltaSeconds,
            maximumCompletedTicks: 30_000,
            spatialCellSize: SimulationDefinition.DefaultSpatialCellSize,
            workerCount: 8);

        var sink = new ProbeSink();
        var result = SpaceBattleHost.Run(definition, _root, CancellationToken.None, sink);

        TestContext.Progress.WriteLine($"STALLPROBE completed_ticks={result.CompletedTicks} remaining={result.RemainingShips} samples={sink.Samples.Count}");
        long lastDamage = -1;
        var sameDamageStreak = 0;
        long stallStartTick = -1;
        foreach (var sample in sink.Samples)
        {
            TestContext.Progress.WriteLine(
                $"STALLPROBE tick={sample.Tick} alive={sample.Alive} damage={sample.Damage} deaths={sample.Deaths} in_range={sample.InRange} wand={sample.Wandering} atk={sample.Attacking}");
            if (sample.Damage == lastDamage)
            {
                sameDamageStreak++;
                if (sameDamageStreak >= 4 && stallStartTick < 0)
                {
                    stallStartTick = sample.Tick - (125L * 3);
                }
            }
            else
            {
                sameDamageStreak = 0;
            }

            lastDamage = sample.Damage;
        }

        TestContext.Progress.WriteLine(
            $"STALLPROBE damage_stalled_at={stallStartTick.ToString(CultureInfo.InvariantCulture)} damage_total={lastDamage.ToString(CultureInfo.InvariantCulture)}");

        var databaseDirectory = SpaceBattlePaths.DatabaseDirectory(_root);
        using var engine = SpaceBattleDatabase.Open(definition, databaseDirectory);
        using var readTransaction = engine.CreateReadOnlyTransaction();
        var total = 0;
        var zeroHealth = 0;
        var engineAlive = 0;
        var aliveKeys = new HashSet<long>();
        foreach (var entityId in readTransaction.QueryExact<Ship>().Execute())
        {
            total++;
            if (readTransaction.Open(entityId).Read(Ship.Vitals).CurrentHealth == 0)
            {
                zeroHealth++;
            }
            else
            {
                engineAlive++;
                aliveKeys.Add(entityId.EntityKey);
            }
        }

        var memoryAliveKeys = sink.LastPublishedSnapshot is null
            ? []
            : sink.LastPublishedSnapshot.Ships.Select(static ship => ship.EntityKey).ToHashSet();
        var overlap = memoryAliveKeys.Count(memoryAliveKeys.Contains) +
                      aliveKeys.Count(memoryAliveKeys.Contains); // 避免重复计数，仅显示对称差异
        var onlyEngine = aliveKeys.Where(key => !memoryAliveKeys.Contains(key)).Take(20).ToArray();
        var onlyMemory = memoryAliveKeys.Where(key => !aliveKeys.Contains(key)).Take(20).ToArray();
        TestContext.Progress.WriteLine(
            $"STALLPROBE engine_total={total} engine_zero_health={zeroHealth} engine_alive={engineAlive} memory_alive={memoryAliveKeys.Count} " +
            $"only_engine_sample=[{string.Join(",", onlyEngine)}] only_memory_sample=[{string.Join(",", onlyMemory)}] overlap={overlap}");
    }

    private sealed class ProbeSink : ISpaceBattleObservationSink
    {
        public List<Sample> Samples { get; } = [];

        public SpaceBattleSnapshot LastPublishedSnapshot { get; private set; }

        public void Publish(SpaceBattleObservation observation)
        {
            if (observation is not SimulationTickCompleted tick)
            {
                return;
            }

            if (tick.Telemetry is not null)
            {
                Samples.Add(new Sample(
                    tick.TickNumber,
                    tick.Telemetry.AliveShips,
                    tick.Telemetry.Combat.Damage,
                    tick.Telemetry.Combat.Deaths,
                    tick.Telemetry.Combat.InRangeAttacks,
                    tick.Telemetry.WanderingNextTick,
                    tick.Telemetry.AttackingNextTick));
            }

            if (tick.PublishedSnapshot is not null)
            {
                LastPublishedSnapshot = tick.PublishedSnapshot;
            }
        }
    }

    // 引擎层最小复现：不经 SpaceBattleHost/runtime，直接 SpawnBatch + WriteTickFence(0)，
    // 然后检查簇 Vitals 列分布——隔离「簇 Vitals 同步缺口」在引擎 API 层还是 runtime 层。
    [Test]
    [Explicit("临时诊断探针：引擎层簇 Vitals 同步检查；Release-only。")]
    public void EngineMinimal_SpawnBatchFence_ClusterVitalsDistribution()
    {
        const int shipCount = 5_000;
        var definition = new SimulationDefinition(shipCount);

        var directory = Path.Combine(_root, "engine-minimal");
        using var engine = SpaceBattleDatabase.Open(definition, directory);

        var hulls = new Hull[shipCount];
        var motions = new Motion[shipCount];
        var vitals = new Vitals[shipCount];
        var targetings = new Targeting[shipCount];
        var behaviors = new Behavior[shipCount];
        var random = new SplitMix64(definition.Seed);
        for (var index = 0; index < shipCount; index++)
        {
            var x = random.NextCoordinate(definition.WorldWidth);
            var y = random.NextCoordinate(definition.WorldHeight);
            var z = random.NextCoordinate(definition.WorldDepth);
            hulls[index] = new Hull
            {
                Bounds = new AABB3F
                {
                    MinX = x,
                    MinY = y,
                    MinZ = z,
                    MaxX = x,
                    MaxY = y,
                    MaxZ = z,
                },
            };
            vitals[index] = new Vitals { CurrentHealth = definition.MaximumHealth };
            behaviors[index] = new Behavior
            {
                Mode = (byte)BehaviorMode.Wandering,
                Phase = (byte)BehaviorPhase.Ready,
            };
        }

        var ids = new EntityId[shipCount];
        using (var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            transaction.SpawnBatchAllocate<Ship>(shipCount, ids);
            transaction.SpawnBatchWriteAll(0, shipCount, Ship.Hull, hulls);
            transaction.SpawnBatchWriteAll(0, shipCount, Ship.Motion, motions);
            transaction.SpawnBatchWriteAll(0, shipCount, Ship.Vitals, vitals);
            transaction.SpawnBatchWriteAll(0, shipCount, Ship.Targeting, targetings);
            transaction.SpawnBatchWriteAll(0, shipCount, Ship.Behavior, behaviors);
            Assert.That(transaction.Commit(), Is.True);
        }

        engine.WriteTickFence(0);

        using var readTransaction = engine.CreateReadOnlyTransaction();
        var total = 0;
        var zeroHealth = 0;
        var firstZeroKeys = new List<long>();
        foreach (var entityId in readTransaction.QueryExact<Ship>().Execute())
        {
            total++;
            if (readTransaction.Open(entityId).Read(Ship.Vitals).CurrentHealth == 0)
            {
                zeroHealth++;
                if (firstZeroKeys.Count < 24)
                {
                    firstZeroKeys.Add(entityId.EntityKey);
                }
            }
        }

        TestContext.Progress.WriteLine(
            $"ENGINEMINIMAL total={total} zero_vitals={zeroHealth} zero_ratio={((double)zeroHealth / Math.Max(1, total)):P1} " +
            $"first_zero_keys=[{string.Join(",", firstZeroKeys)}]");

        // 与 demo Publish 同源：cluster SoA 直接读（不经只读事务视图）。
        using var accessor = new PointInTimeAccessor();
        accessor.Attach(engine, 1);
        var entityAccessor = accessor.GetWorkerAccessor(0);
        using var archetype = entityAccessor.For<Ship>();
        var clusterCount = archetype.ClusterCount;
        var slotsInCluster = 0;
        var zeroInCluster = 0;
        for (var chunk = 0; chunk < clusterCount; chunk++)
        {
            using var clusters = entityAccessor.GetClusterEnumerator<Ship>(chunk, chunk + 1);
            while (clusters.MoveNext())
            {
                var cluster = clusters.Current;
                var vitalsSpan = cluster.GetReadOnlySpan(Ship.Vitals);
                foreach (var slot in new SpaceBattleOccupiedSlots(cluster.OccupancyBits))
                {
                    slotsInCluster++;
                    if (vitalsSpan[slot].CurrentHealth == 0)
                    {
                        zeroInCluster++;
                    }
                }
            }
        }

        TestContext.Progress.WriteLine(
            $"ENGINEMINIMAL_CLUSTER clusters={clusterCount} slots={slotsInCluster} zero_in_cluster={zeroInCluster} " +
            $"zero_ratio={((double)zeroInCluster / Math.Max(1, slotsInCluster)):P1}");

        // 实验：簇 SoA 直接写（Damage 同路径）能否修复 0 槽？
        var zeroKeysByChunk = new Dictionary<int, List<long>>();
        for (var chunk = 0; chunk < clusterCount; chunk++)
        {
            using var clustersScan = entityAccessor.GetClusterEnumerator<Ship>(chunk, chunk + 1);
            while (clustersScan.MoveNext())
            {
                var cluster = clustersScan.Current;
                var vitalsSpanScan = cluster.GetReadOnlySpan(Ship.Vitals);
                foreach (var slot in new SpaceBattleOccupiedSlots(cluster.OccupancyBits))
                {
                    if (vitalsSpanScan[slot].CurrentHealth == 0)
                    {
                        if (!zeroKeysByChunk.TryGetValue(chunk, out var keys))
                        {
                            keys = [];
                            zeroKeysByChunk[chunk] = keys;
                        }

                        keys.Add(cluster.GetEntityId(slot).EntityKey);
                    }
                }
            }
        }

        var repairedTotal = 0;
        foreach (var (chunk, keys) in zeroKeysByChunk)
        {
            using var clustersFix = entityAccessor.GetClusterEnumerator<Ship>(chunk, chunk + 1);
            while (clustersFix.MoveNext())
            {
                var cluster = clustersFix.Current;
                var vitalsSpanFix = cluster.GetSpan(Ship.Vitals);
                var fixedInCluster = 0;
                foreach (var slot in new SpaceBattleOccupiedSlots(cluster.OccupancyBits))
                {
                    var entityKey = cluster.GetEntityId(slot).EntityKey;
                    if (keys.Contains(entityKey))
                    {
                        var correct = readTransaction.Open(cluster.GetEntityId(slot)).Read(Ship.Vitals);
                        vitalsSpanFix[slot] = correct;
                        fixedInCluster++;
                    }
                }

                if (fixedInCluster > 0)
                {
                    clustersFix.MarkCurrentDirty();
                    repairedTotal += fixedInCluster;
                }
            }
        }

        TestContext.Progress.WriteLine($"ENGINEMINIMAL_REPAIR cluster_wrote={repairedTotal}");
        engine.WriteTickFence(0);

        var zeroAfterRepair = 0;
        var entityAccessorAfter = accessor.GetWorkerAccessor(0);
        using var archetypeAfter = entityAccessorAfter.For<Ship>();
        for (var chunk = 0; chunk < archetypeAfter.ClusterCount; chunk++)
        {
            using var clustersAfter = entityAccessorAfter.GetClusterEnumerator<Ship>(chunk, chunk + 1);
            while (clustersAfter.MoveNext())
            {
                var cluster = clustersAfter.Current;
                var vitalsSpanAfter = cluster.GetReadOnlySpan(Ship.Vitals);
                foreach (var slot in new SpaceBattleOccupiedSlots(cluster.OccupancyBits))
                {
                    if (vitalsSpanAfter[slot].CurrentHealth == 0)
                    {
                        zeroAfterRepair++;
                    }
                }
            }
        }

        TestContext.Progress.WriteLine(
            $"ENGINEMINIMAL_AFTER_REPAIR zero_in_cluster={zeroAfterRepair}");
    }

    // 运行期恶化复现实验：纯引擎 API 下，模拟 Damage 全量写簇 Vitals + fence 迭代，
    // 观察簇 SoA 读视图是否丢失写入（归 0/回退）——回答「恶化是否需要 runtime 时序」。
    [Test]
    [Explicit("临时诊断探针：fence 迭代下簇 SoA Vitals 恶化复现；Release-only。")]
    public void EngineMinimal_FenceIterations_ClusterVitalsDegradation()
    {
        const int shipCount = 5_000;
        var definition = new SimulationDefinition(shipCount);

        var directory = Path.Combine(_root, "engine-fence-iter");
        using var engine = SpaceBattleDatabase.Open(definition, directory);

        var hulls = new Hull[shipCount];
        var motions = new Motion[shipCount];
        var vitals = new Vitals[shipCount];
        var targetings = new Targeting[shipCount];
        var behaviors = new Behavior[shipCount];
        var random = new SplitMix64(definition.Seed);
        for (var index = 0; index < shipCount; index++)
        {
            var x = random.NextCoordinate(definition.WorldWidth);
            var y = random.NextCoordinate(definition.WorldHeight);
            var z = random.NextCoordinate(definition.WorldDepth);
            hulls[index] = new Hull
            {
                Bounds = new AABB3F
                {
                    MinX = x,
                    MinY = y,
                    MinZ = z,
                    MaxX = x,
                    MaxY = y,
                    MaxZ = z,
                },
            };
            vitals[index] = new Vitals { CurrentHealth = definition.MaximumHealth };
            behaviors[index] = new Behavior
            {
                Mode = (byte)BehaviorMode.Wandering,
                Phase = (byte)BehaviorPhase.Ready,
            };
        }

        var ids = new EntityId[shipCount];
        using (var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            transaction.SpawnBatchAllocate<Ship>(shipCount, ids);
            transaction.SpawnBatchWriteAll(0, shipCount, Ship.Hull, hulls);
            transaction.SpawnBatchWriteAll(0, shipCount, Ship.Motion, motions);
            transaction.SpawnBatchWriteAll(0, shipCount, Ship.Vitals, vitals);
            transaction.SpawnBatchWriteAll(0, shipCount, Ship.Targeting, targetings);
            transaction.SpawnBatchWriteAll(0, shipCount, Ship.Behavior, behaviors);
            Assert.That(transaction.Commit(), Is.True);
        }

        engine.WriteTickFence(0);

        using var accessor = new PointInTimeAccessor();
        accessor.Attach(engine, 1);
        var entityAccessor = accessor.GetWorkerAccessor(0);

        (int Slots, int Zero) Scan()
        {
            var slots = 0;
            var zero = 0;
            using var arch = entityAccessor.For<Ship>();
            for (var chunk = 0; chunk < arch.ClusterCount; chunk++)
            {
                using var clusters = entityAccessor.GetClusterEnumerator<Ship>(chunk, chunk + 1);
                while (clusters.MoveNext())
                {
                    var cluster = clusters.Current;
                    var span = cluster.GetReadOnlySpan(Ship.Vitals);
                    foreach (var slot in new SpaceBattleOccupiedSlots(cluster.OccupancyBits))
                    {
                        slots++;
                        if (span[slot].CurrentHealth == 0)
                        {
                            zero++;
                        }
                    }
                }
            }

            return (slots, zero);
        }

        var afterSpawn = Scan();
        TestContext.Progress.WriteLine(
            $"FENCEITER after_spawn slots={afterSpawn.Slots} zero={afterSpawn.Zero} ratio={(afterSpawn.Slots == 0 ? 0 : (double)afterSpawn.Zero / afterSpawn.Slots):P1}");

        // 修复 0 槽（簇写满血 + MarkCurrentDirty + fence）
        using (var arch = entityAccessor.For<Ship>())
        {
            for (var chunk = 0; chunk < arch.ClusterCount; chunk++)
            {
                using var clusters = entityAccessor.GetClusterEnumerator<Ship>(chunk, chunk + 1);
                while (clusters.MoveNext())
                {
                    var cluster = clusters.Current;
                    var span = cluster.GetSpan(Ship.Vitals);
                    var fixedAny = false;
                    foreach (var slot in new SpaceBattleOccupiedSlots(cluster.OccupancyBits))
                    {
                        if (span[slot].CurrentHealth == 0)
                        {
                            span[slot] = new Vitals { CurrentHealth = definition.MaximumHealth };
                            fixedAny = true;
                        }
                    }

                    if (fixedAny)
                    {
                        clusters.MarkCurrentDirty();
                    }
                }
            }
        }

        engine.WriteTickFence(0);
        var afterRepair = Scan();
        TestContext.Progress.WriteLine(
            $"FENCEITER after_repair slots={afterRepair.Slots} zero={afterRepair.Zero} ratio={(afterRepair.Slots == 0 ? 0 : (double)afterRepair.Zero / afterRepair.Slots):P1}");

        // 模拟 Damage：全量写簇 Vitals=500 + fence，观察保留率（500 保留 = 簇写路径正常；归 0 = 恶化复现）
        for (var round = 1; round <= 6; round++)
        {
            using (var arch = entityAccessor.For<Ship>())
            {
                for (var chunk = 0; chunk < arch.ClusterCount; chunk++)
                {
                    using var clusters = entityAccessor.GetClusterEnumerator<Ship>(chunk, chunk + 1);
                    while (clusters.MoveNext())
                    {
                        var cluster = clusters.Current;
                        var span = cluster.GetSpan(Ship.Vitals);
                        var wrote = false;
                        foreach (var slot in new SpaceBattleOccupiedSlots(cluster.OccupancyBits))
                        {
                            span[slot] = new Vitals { CurrentHealth = 500u };
                            wrote = true;
                        }

                        if (wrote)
                        {
                            clusters.MarkCurrentDirty();
                        }
                    }
                }
            }

            engine.WriteTickFence(0);

            var scan = Scan();
            var preserved = 0;
            using (var arch = entityAccessor.For<Ship>())
            {
                for (var chunk = 0; chunk < arch.ClusterCount; chunk++)
                {
                    using var clusters = entityAccessor.GetClusterEnumerator<Ship>(chunk, chunk + 1);
                    while (clusters.MoveNext())
                    {
                        var cluster = clusters.Current;
                        var span = cluster.GetReadOnlySpan(Ship.Vitals);
                        foreach (var slot in new SpaceBattleOccupiedSlots(cluster.OccupancyBits))
                        {
                            if (span[slot].CurrentHealth == 500u)
                            {
                                preserved++;
                            }
                        }
                    }
                }
            }

            TestContext.Progress.WriteLine(
                $"FENCEITER round={round} zero={scan.Zero}/{scan.Slots} " +
                $"preserved_500={preserved}/{scan.Slots} ratio={(scan.Slots == 0 ? 0 : (double)preserved / scan.Slots):P1}");
        }
    }

    private readonly record struct Sample(
        long Tick,
        int Alive,
        long Damage,
        long Deaths,
        long InRange,
        int Wandering,
        int Attacking);
}