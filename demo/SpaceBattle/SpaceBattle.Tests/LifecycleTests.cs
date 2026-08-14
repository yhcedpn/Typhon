using System.Threading;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class LifecycleTests
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
    public void Run_WithTwoShipsAndAOneTickLimitReportsTheLimit()
    {
        var definition = CreateDefinition(shipCount: 2, maximumCompletedTicks: 1);

        var result = SpaceBattleHost.Run(definition, _root, CancellationToken.None, new RecordingSink());

        Assert.Multiple(() =>
        {
            Assert.That(result.TerminationReason, Is.EqualTo(SpaceBattleTerminationReason.TickLimit));
            Assert.That(result.CompletedTicks, Is.EqualTo(1));
            Assert.That(result.RemainingShips, Is.GreaterThanOrEqualTo(2));
        });
    }

    [Test]
    public void Run_CloseShipsReportsBattleOutcomeBeforeTheTickLimit()
    {
        var definition = new SimulationDefinition(
            shipCount: 2,
            worldWidth: 1f,
            worldHeight: 1f,
            worldDepth: 1f,
            maximumHealth: 1_000,
            maximumCompletedTicks: 130);

        var result = SpaceBattleHost.Run(definition, _root, CancellationToken.None, new RecordingSink());

        Assert.Multiple(() =>
        {
            Assert.That(result.TerminationReason, Is.AnyOf(
                SpaceBattleTerminationReason.Draw,
                SpaceBattleTerminationReason.Winner));
            Assert.That(result.CompletedTicks, Is.LessThan(130));
            Assert.That(result.RemainingShips, Is.LessThan(2));
        });
    }

    [Test]
    public void Run_CancellationReturnsAfterTheCurrentTickBoundary()
    {
        using var cancellation = new CancellationTokenSource();
        var sink = new CancellingSink(cancellation);
        var definition = CreateDefinition(shipCount: 2, maximumCompletedTicks: 100);

        var result = SpaceBattleHost.Run(definition, _root, cancellation.Token, sink);

        Assert.Multiple(() =>
        {
            Assert.That(result.TerminationReason, Is.EqualTo(SpaceBattleTerminationReason.Cancelled));
            Assert.That(result.CompletedTicks, Is.EqualTo(1));
            Assert.That(sink.TickCount, Is.EqualTo(1));
            Assert.That(result.RemainingShips, Is.GreaterThanOrEqualTo(2));
        });
    }

    [Test]
    public void AbortTickAndStopDoesNotRunFollowingSystemsOrTicksAndKeepsEarlierCommit()
    {
        var definition = CreateDefinition(shipCount: 2, maximumCompletedTicks: 2);
        SpaceBattleHost.BootstrapOnly(definition, _root, CancellationToken.None, new RecordingSink());
        var databaseDirectory = SpaceBattlePaths.DatabaseDirectory(_root);

        using var engine = SpaceBattleDatabase.Open(definition, databaseDirectory);
        var entityId = ReadShipIds(engine).First();
        var successfulSystemRuns = 0;
        var followingSystemRuns = 0;
        var aborted = new ManualResetEventSlim(false);
        TickOutcome outcome = default;

        using var runtime = TyphonRuntime.Create(
            engine,
            schedule =>
            {
                schedule.PublicTrack.DeclareDag("FatalLifecycle")
                    .CallbackSystem("CommitBeforeFatal", context =>
                    {
                        Interlocked.Increment(ref successfulSystemRuns);
                        ref var behavior = ref context.Transaction.OpenMut(entityId).Write(Ship.Behavior);
                        behavior.ModeStartedTick = 99;
                    })
                    .CallbackSystem("Fatal", _ => throw new InvalidOperationException("fatal lifecycle test"), after: "CommitBeforeFatal")
                    .CallbackSystem("Following", _ => Interlocked.Increment(ref followingSystemRuns), after: "Fatal");
            },
            new RuntimeOptions
            {
                WorkerCount = 1,
                BaseTickRate = 1_000,
                SystemExceptionPolicy = SystemExceptionPolicy.AbortTickAndStop,
            });

        runtime.OnTickAborted += (_, tickOutcome) =>
        {
            outcome = tickOutcome;
            aborted.Set();
        };
        runtime.Start();
        Assert.That(aborted.Wait(TimeSpan.FromSeconds(5)), Is.True);
        var runsAtAbort = Volatile.Read(ref successfulSystemRuns);
        var tickAtAbort = runtime.CurrentTickNumber;
        runtime.FatalStop();
        Assert.That(runtime.CurrentTickNumber, Is.EqualTo(tickAtAbort));

        Behavior persistedBehavior;
        using (var transaction = engine.CreateReadOnlyTransaction())
        {
            persistedBehavior = transaction.Open(entityId).Read(Ship.Behavior);
        }

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Reason, Is.EqualTo(TickOutcomeReason.SystemException));
            Assert.That(outcome.FailedSystemName, Is.EqualTo("Fatal"));
            Assert.That(outcome.FailedSystemException?.ToString(), Does.Contain("fatal lifecycle test"));
            Assert.That(Volatile.Read(ref successfulSystemRuns), Is.EqualTo(runsAtAbort));
            Assert.That(Volatile.Read(ref followingSystemRuns), Is.Zero);
            Assert.That(persistedBehavior.ModeStartedTick, Is.EqualTo(99));
        });
    }

    private static SimulationDefinition CreateDefinition(int shipCount, ulong maximumCompletedTicks) =>
        new(
            shipCount: shipCount,
            seed: SimulationDefinition.DefaultSeed,
            worldWidth: 1_000f,
            worldHeight: 1_000f,
            worldDepth: 400f,
            maximumHealth: 1_000,
            maximumCompletedTicks: maximumCompletedTicks);

    private static EntityId[] ReadShipIds(DatabaseEngine engine)
    {
        using var transaction = engine.CreateReadOnlyTransaction();
        return transaction.QueryExact<Ship>().Execute()
            .OrderBy(static id => id.EntityKey)
            .ToArray();
    }

    private sealed class RecordingSink : ISpaceBattleObservationSink
    {
        public void Publish(SpaceBattleObservation observation)
        {
        }
    }

    private sealed class CancellingSink(CancellationTokenSource cancellation) : ISpaceBattleObservationSink
    {
        public int TickCount { get; private set; }

        public void Publish(SpaceBattleObservation observation)
        {
            if (observation is SimulationTickCompleted)
            {
                TickCount++;
                cancellation.Cancel();
            }
        }
    }
}
