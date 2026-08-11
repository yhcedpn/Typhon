using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle.TransientViewPrototype;

[Component("SpaceBattle.PrototypeRunAuthority", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct RunAuthorityComponent
{
    [Field] public long Marker;
}

[Component("SpaceBattle.PrototypeShipAuthority", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct ShipAuthorityComponent
{
    [Field] public long RunEntityKey;
}

[Component(
    "SpaceBattle.ShipMembership",
    1,
    StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
public struct ShipMembershipComponent
{
    [Field]
    [Index(AllowMultiple = true)]
    public long RunEntityKey;
}

[Archetype(3900)]
public sealed partial class PrototypeRun : Archetype<PrototypeRun>
{
    public static readonly Comp<RunAuthorityComponent> Authority = Register<RunAuthorityComponent>();
}

[Archetype(3901)]
public sealed partial class Ship : Archetype<Ship>
{
    public static readonly Comp<ShipAuthorityComponent> Authority = Register<ShipAuthorityComponent>();
    public static readonly Comp<ShipMembershipComponent> Membership = Register<ShipMembershipComponent>();
}

internal static class Program
{
    private const int RuntimeWaitTimeoutSeconds = 5;

    public static int Main()
    {
        var scratchRoot = Path.Combine(
            Path.GetTempPath(),
            $"typhon-spacebattle-transient-view-PROTOTYPE-WIPE-ME-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(scratchRoot, "database");
        Directory.CreateDirectory(scratchRoot);

        Console.WriteLine("PROTOTYPE — SpaceBattle Transient membership view");
        Console.WriteLine("问题：恢复后重建 Transient membership 时，Commit、tick fence、view 创建和 runtime refresh 的最小正确顺序是什么？");
        Console.WriteLine($"临时数据库：{databasePath}");

        try
        {
            var seeded = RunFreshDatabaseScenarios(databasePath);
            RunRecoveryScenarios(databasePath, seeded.RunEntityKey, seeded.AuthoritativeShipCount);
            Console.WriteLine();
            Console.WriteLine("VERDICT: PROTOTYPE COMPLETED — 详细结论见上方每个场景的状态证据。");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"PROTOTYPE FAILED: {exception}");
            return 1;
        }
        finally
        {
            if (Directory.Exists(scratchRoot))
            {
                Directory.Delete(scratchRoot, recursive: true);
            }
        }
    }

    private static SeedResult RunFreshDatabaseScenarios(string databasePath)
    {
        PrintScenario("1. 新数据库：Commit 后立即创建增量 view");
        using var engine = Open(databasePath);

        EntityId runId;
        var initialShipIds = new List<EntityId>();
        using (var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            var run = new RunAuthorityComponent { Marker = 1 };
            runId = transaction.Spawn<PrototypeRun>(PrototypeRun.Authority.Set(in run));

            for (var index = 0; index < 3; index++)
            {
                initialShipIds.Add(SpawnShip(transaction, runId.EntityKey));
            }

            transaction.Commit();
        }

        using var viewTransaction = engine.CreateQuickTransaction();
        using var firstView = CreateMembershipView(viewTransaction, runId.EntityKey);
        using var secondView = CreateMembershipView(viewTransaction, runId.EntityKey);
        Check(firstView.Count == 3, "新实体的 Transient index 在 spawn Commit 时已可见，创建 view 不需要 tick fence", $"view.Count={firstView.Count}");
        Check(secondView.Count == 3, "两个同谓词 view 都完成初始填充", $"first={firstView.Count}, second={secondView.Count}");

        PrintScenario("2. view 创建后 Spawn/Destroy：两个订阅者的 Added/Removed");
        EntityId spawnedAfterView;
        using (var transaction = engine.CreateQuickTransaction())
        {
            spawnedAfterView = SpawnShip(transaction, runId.EntityKey);
            transaction.Commit();
        }

        RefreshBoth(engine, firstView, secondView);
        CheckViewDelta(firstView, expectedCount: 4, expectedAdded: 1, expectedRemoved: 0, "第一个 view 收到 Spawn Added");
        CheckViewDelta(secondView, expectedCount: 4, expectedAdded: 1, expectedRemoved: 0, "第二个 view 独立收到 Spawn Added");

        using (var transaction = engine.CreateQuickTransaction())
        {
            transaction.Destroy(spawnedAfterView);
            transaction.Commit();
        }

        RefreshBoth(engine, firstView, secondView);
        CheckViewDelta(firstView, expectedCount: 3, expectedAdded: 0, expectedRemoved: 1, "第一个 view 收到 Destroy Removed");
        CheckViewDelta(secondView, expectedCount: 3, expectedAdded: 0, expectedRemoved: 1, "第二个 view 独立收到 Destroy Removed");

        using var verify = engine.CreateReadOnlyTransaction();
        var authoritativeCount = verify.Query<Ship>().Count();
        Check(authoritativeCount == initialShipIds.Count, "销毁后权威 Ship 数量正确", $"authoritative={authoritativeCount}");
        return new SeedResult(runId.EntityKey, authoritativeCount);
    }

    private static void RunRecoveryScenarios(string databasePath, long expectedRunEntityKey, int expectedShipCount)
    {
        PrintScenario("3. 已有数据库重开：Transient 归零，Commit 与 tick fence 可见性分界");
        using var engine = Open(databasePath);

        EntityId runId;
        List<EntityId> shipIds;
        using (var read = engine.CreateReadOnlyTransaction())
        {
            runId = read.Query<PrototypeRun>().Execute().Single();
            shipIds = read.Query<Ship>().Execute().OrderBy(static id => id.EntityKey).ToList();

            Check(runId.EntityKey == expectedRunEntityKey, "恢复后的 run EntityKey 保持不变", $"run={runId.EntityKey}");
            Check(shipIds.Count == expectedShipCount, "权威 Ship 实体跨重开保留", $"ships={shipIds.Count}");
            Check(
                shipIds.All(id => read.Open(id).Read(Ship.Membership).RunEntityKey == 0),
                "Transient membership 在重开后归零",
                $"zeroed={shipIds.Count}");
        }

        using (var read = engine.CreateQuickTransaction())
        {
            var zeroIndexCount = read.Query<Ship>()
                .WhereField<ShipMembershipComponent>(membership => membership.RunEntityKey == 0)
                .Count();
            var targetIndexCount = MembershipCount(read, runId.EntityKey);
            Check(
                zeroIndexCount == expectedShipCount && targetIndexCount == 0,
                "重开阶段从归零 Transient 数据重建出 key=0 index；目标 run key 尚不可见",
                $"zeroKey={zeroIndexCount}, runKey={targetIndexCount}");
        }

        using (var rebuild = engine.CreateQuickTransaction())
        {
            foreach (var shipId in shipIds)
            {
                rebuild.OpenMut(shipId).Write(Ship.Membership).RunEntityKey = runId.EntityKey;
            }

            rebuild.Commit();
        }

        using (var afterCommit = engine.CreateQuickTransaction())
        {
            var targetCount = MembershipCount(afterCommit, runId.EntityKey);
            var zeroCount = MembershipCount(afterCommit, 0);
            Check(
                targetCount == expectedShipCount && zeroCount == 0,
                "仅 Commit 已让 WhereField 观察到全部重建值",
                $"runKeyCount={targetCount}, zeroKeyCount={zeroCount}");
        }

        using var preFenceViewTransaction = engine.CreateQuickTransaction();
        using var preFenceView = CreateMembershipView(preFenceViewTransaction, runId.EntityKey);
        Check(
            preFenceView.Count == expectedShipCount,
            "Commit 后、fence 前创建的 view 已有完整初始集合",
            $"view.Count={preFenceView.Count}");

        var fenceLsn = engine.WriteTickFence(1);
        using (var afterFence = engine.CreateQuickTransaction())
        {
            Check(
                MembershipCount(afterFence, runId.EntityKey) == expectedShipCount,
                "WriteTickFence 排空 shadow entries 后，目标 run key index 完整可见",
                $"runKeyCount={MembershipCount(afterFence, runId.EntityKey)}, fenceLsn={fenceLsn}");
        }

        using (var refresh = engine.CreateQuickTransaction())
        {
            preFenceView.Refresh(refresh);
        }
        var fenceDelta = preFenceView.GetDelta();
        Check(
            preFenceView.Count == expectedShipCount,
            "fence 前已注册的 view 在 fence 后 Refresh 仍保持完整",
            $"count={preFenceView.Count}, added={fenceDelta.Added.Count}, removed={fenceDelta.Removed.Count}, modified={fenceDelta.Modified.Count}");

        using var postFenceViewTransaction = engine.CreateQuickTransaction();
        using var firstView = CreateMembershipView(postFenceViewTransaction, runId.EntityKey);
        using var secondView = CreateMembershipView(postFenceViewTransaction, runId.EntityKey);
        Check(
            firstView.Count == expectedShipCount && secondView.Count == expectedShipCount,
            "Commit → WriteTickFence → ToView 也得到完整集合，并避开未 drain 的 fence delta",
            $"first={firstView.Count}, second={secondView.Count}");

        PrintScenario("4. runtime system input：候选 view 原样接入时不会自动 drain");
        RunRuntimeWithoutExplicitRefresh(engine, runId.EntityKey, firstView, secondView, expectedShipCount);

        PrintScenario("5. runtime 显式 refresh 阶段：Spawn/Destroy 增量传播");
        RunRuntimeWithExplicitRefresh(engine, runId.EntityKey, firstView, secondView, expectedShipCount + 1);
    }

    private static void RunRuntimeWithoutExplicitRefresh(
        DatabaseEngine engine,
        long runEntityKey,
        EcsView<Ship> firstView,
        EcsView<Ship> secondView,
        int initialCount)
    {
        var ticksSeen = 0;
        var systemCount = -1;
        EntityId spawned;

        using (var runtime = TyphonRuntime.Create(engine, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("PrototypeNoRefresh");
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            dag.QuerySystem(
                "ObserveMembership",
                context => Volatile.Write(ref systemCount, context.Entities.Count),
                input: () => firstView,
                parallel: true,
                after: "Tick");
        }, RuntimeOptions()))
        {
            runtime.Start();
            WaitUntil(() => Volatile.Read(ref ticksSeen) >= 2, "runtime 未完成初始 tick");
            Check(Volatile.Read(ref systemCount) == initialCount, "runtime 首先看到 view 的初始集合", $"systemCount={systemCount}");

            using (var transaction = engine.CreateQuickTransaction())
            {
                spawned = SpawnShip(transaction, runEntityKey);
                transaction.Commit();
            }

            var targetTick = Volatile.Read(ref ticksSeen) + 5;
            WaitUntil(() => Volatile.Read(ref ticksSeen) >= targetTick, "runtime 未推进到 Spawn 后的观察点");
            runtime.Shutdown();
        }

        Check(
            firstView.Count == initialCount && Volatile.Read(ref systemCount) == initialCount,
            "增量 view 作为 system input 时，runtime 不会自动调用 Refresh；成员仍停留在旧集合",
            $"view={firstView.Count}, system={systemCount}, expectedDatabase={initialCount + 1}");

        RefreshBoth(engine, firstView, secondView);
        CheckViewDelta(firstView, initialCount + 1, expectedAdded: 1, expectedRemoved: 0, "手动 Refresh 后第一个 view drain Spawn delta");
        CheckViewDelta(secondView, initialCount + 1, expectedAdded: 1, expectedRemoved: 0, "手动 Refresh 后第二个 view drain Spawn delta");

        using var verify = engine.CreateReadOnlyTransaction();
        Check(verify.Query<Ship>().Count() == initialCount + 1, "未自动 refresh 只影响 view，不影响权威实体", $"spawned={spawned.EntityKey}");
    }

    private static void RunRuntimeWithExplicitRefresh(
        DatabaseEngine engine,
        long runEntityKey,
        EcsView<Ship> firstView,
        EcsView<Ship> secondView,
        int initialCount)
    {
        var ticksSeen = 0;
        var systemCount = -1;
        var firstAdded = 0;
        var secondAdded = 0;
        var firstRemoved = 0;
        var secondRemoved = 0;

        using var runtime = TyphonRuntime.Create(engine, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("PrototypeExplicitRefresh");
            dag.CallbackSystem("RefreshMembershipViews", context =>
            {
                firstView.Refresh(context.Transaction);
                secondView.Refresh(context.Transaction);

                var firstDelta = firstView.GetDelta();
                var secondDelta = secondView.GetDelta();
                Interlocked.Add(ref firstAdded, firstDelta.Added.Count);
                Interlocked.Add(ref secondAdded, secondDelta.Added.Count);
                Interlocked.Add(ref firstRemoved, firstDelta.Removed.Count);
                Interlocked.Add(ref secondRemoved, secondDelta.Removed.Count);
                Interlocked.Increment(ref ticksSeen);
            });
            dag.QuerySystem(
                "ObserveMembership",
                context => Volatile.Write(ref systemCount, context.Entities.Count),
                input: () => firstView,
                parallel: true,
                after: "RefreshMembershipViews");
        }, RuntimeOptions());

        runtime.Start();
        WaitUntil(
            () => Volatile.Read(ref ticksSeen) >= 2 && Volatile.Read(ref systemCount) == initialCount,
            "显式 refresh runtime 未看到初始集合");

        EntityId spawned;
        using (var transaction = engine.CreateQuickTransaction())
        {
            spawned = SpawnShip(transaction, runEntityKey);
            transaction.Commit();
        }

        WaitUntil(
            () => Volatile.Read(ref systemCount) == initialCount + 1 &&
                firstView.Count == initialCount + 1 &&
                secondView.Count == initialCount + 1 &&
                Volatile.Read(ref firstAdded) >= 1 &&
                Volatile.Read(ref secondAdded) >= 1,
            "显式 refresh 阶段未传播 Spawn");
        Check(
            true,
            "显式 refresh 阶段在 QuerySystem 消费前向两个 view 传播 Added",
            $"system={systemCount}, first={firstView.Count}, second={secondView.Count}, added={firstAdded}/{secondAdded}");

        using (var transaction = engine.CreateQuickTransaction())
        {
            transaction.Destroy(spawned);
            transaction.Commit();
        }

        WaitUntil(
            () => Volatile.Read(ref systemCount) == initialCount &&
                firstView.Count == initialCount &&
                secondView.Count == initialCount &&
                Volatile.Read(ref firstRemoved) >= 1 &&
                Volatile.Read(ref secondRemoved) >= 1,
            "显式 refresh 阶段未传播 Destroy");
        runtime.Shutdown();

        Check(
            true,
            "显式 refresh 阶段在 QuerySystem 消费前向两个 view 传播 Removed",
            $"system={systemCount}, first={firstView.Count}, second={secondView.Count}, removed={firstRemoved}/{secondRemoved}");
    }

    private static DatabaseEngine Open(string databasePath) => DatabaseEngine.Open(
        databasePath,
        options => options
            .Register<RunAuthorityComponent>()
            .Register<ShipAuthorityComponent>()
            .Register<ShipMembershipComponent>());

    private static RuntimeOptions RuntimeOptions() => new()
    {
        BaseTickRate = 250,
        WorkerCount = 1,
        EnableParallelFence = false,
    };

    private static EntityId SpawnShip(Transaction transaction, long runEntityKey)
    {
        var authority = new ShipAuthorityComponent { RunEntityKey = runEntityKey };
        var membership = new ShipMembershipComponent { RunEntityKey = runEntityKey };
        return transaction.Spawn<Ship>(
            Ship.Authority.Set(in authority),
            Ship.Membership.Set(in membership));
    }

    private static EcsView<Ship> CreateMembershipView(Transaction transaction, long runEntityKey) => transaction
        .Query<Ship>()
        .WhereField<ShipMembershipComponent>(membership => membership.RunEntityKey == runEntityKey)
        .ToView();

    private static int MembershipCount(Transaction transaction, long runEntityKey) => transaction
        .Query<Ship>()
        .WhereField<ShipMembershipComponent>(membership => membership.RunEntityKey == runEntityKey)
        .Count();

    private static void RefreshBoth(DatabaseEngine engine, EcsView<Ship> firstView, EcsView<Ship> secondView)
    {
        using var refresh = engine.CreateQuickTransaction();
        firstView.Refresh(refresh);
        secondView.Refresh(refresh);
    }

    private static void CheckViewDelta(
        EcsView<Ship> view,
        int expectedCount,
        int expectedAdded,
        int expectedRemoved,
        string statement)
    {
        var delta = view.GetDelta();
        Check(
            view.Count == expectedCount && delta.Added.Count == expectedAdded && delta.Removed.Count == expectedRemoved,
            statement,
            $"count={view.Count}, added={delta.Added.Count}, removed={delta.Removed.Count}");
    }

    private static void WaitUntil(Func<bool> predicate, string timeoutMessage)
    {
        if (!SpinWait.SpinUntil(predicate, TimeSpan.FromSeconds(RuntimeWaitTimeoutSeconds)))
        {
            throw new TimeoutException(timeoutMessage);
        }
    }

    private static void PrintScenario(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {title} ===");
    }

    private static void Check(bool condition, string statement, string evidence)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"FAIL — {statement}；证据：{evidence}");
        }

        Console.WriteLine($"PASS — {statement}；证据：{evidence}");
    }

    private readonly record struct SeedResult(long RunEntityKey, int AuthoritativeShipCount);
}
