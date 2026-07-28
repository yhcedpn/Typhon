using NUnit.Framework;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class BehaviorRulesTests
{
    [Test]
    public void CreateWanderingMotion_IsRepeatableAndRespectsTheWanderingSpeedLimit()
    {
        var first = BehaviorRules.CreateWanderingMotion(
            SimulationDefinition.DefaultSeed,
            shipId: 0xBBAA,
            decisionOrdinal: 42);
        var second = BehaviorRules.CreateWanderingMotion(
            SimulationDefinition.DefaultSeed,
            shipId: 0xBBAA,
            decisionOrdinal: 42);
        var directionLengthSquared =
            (first.DirectionX * first.DirectionX) +
            (first.DirectionY * first.DirectionY) +
            (first.DirectionZ * first.DirectionZ);

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.EqualTo(first));
            Assert.That(directionLengthSquared, Is.EqualTo(1f).Within(0.00001f));
            Assert.That(first.Speed, Is.InRange(0f, SimulationDefinition.MaximumWanderingSpeed));
        });
    }

    [Test]
    public void DecideWandering_IsRepeatableAndIncludesEverySpecifiedOutcome()
    {
        var decisions = Enumerable.Range(0, 1_024)
            .Select(ordinal => BehaviorRules.DecideWandering(
                SimulationDefinition.DefaultSeed,
                shipId: 0xBBAA,
                decisionOrdinal: (ulong)ordinal))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(
                BehaviorRules.DecideWandering(SimulationDefinition.DefaultSeed, 0xBBAA, 42),
                Is.EqualTo(BehaviorRules.DecideWandering(SimulationDefinition.DefaultSeed, 0xBBAA, 42)));
            Assert.That(decisions, Does.Contain(WanderingDecision.ContinueWandering));
            Assert.That(decisions, Does.Contain(WanderingDecision.Track));
            Assert.That(decisions, Does.Contain(WanderingDecision.Combat));
        });
    }

    [Test]
    public void SelectTrackingTargetIndex_IsRepeatableAndNeverReturnsTheSource()
    {
        const int rosterCount = 7;
        const int sourceIndex = 3;

        var selected = BehaviorRules.SelectTrackingTargetIndex(
            SimulationDefinition.DefaultSeed,
            shipId: 0xBBAA,
            decisionOrdinal: 42,
            rosterCount,
            sourceIndex);

        Assert.Multiple(() =>
        {
            Assert.That(selected, Is.InRange(0, rosterCount - 1));
            Assert.That(selected, Is.Not.EqualTo(sourceIndex));
            Assert.That(
                BehaviorRules.SelectTrackingTargetIndex(
                    SimulationDefinition.DefaultSeed,
                    0xBBAA,
                    42,
                    rosterCount,
                    sourceIndex),
                Is.EqualTo(selected));
        });
    }

    [Test]
    public void SelectLockTargetCandidateIndex_IsRepeatableAndNeverReturnsTheSource()
    {
        const int rosterCount = 97;
        const int sourceIndex = 42;

        var selected = BehaviorRules.SelectLockTargetCandidateIndex(
            SimulationDefinition.DefaultSeed,
            shipId: 0xBBAA,
            decisionOrdinal: 42,
            rosterCount,
            sourceIndex,
            candidateOrdinal: BehaviorRules.MaximumLockCandidatesPerAttempt - 1);

        Assert.Multiple(() =>
        {
            Assert.That(selected, Is.InRange(0, rosterCount - 1));
            Assert.That(selected, Is.Not.EqualTo(sourceIndex));
            Assert.That(
                BehaviorRules.SelectLockTargetCandidateIndex(
                    SimulationDefinition.DefaultSeed,
                    0xBBAA,
                    42,
                    rosterCount,
                    sourceIndex,
                    BehaviorRules.MaximumLockCandidatesPerAttempt - 1),
                Is.EqualTo(selected));
            Assert.That(() => BehaviorRules.SelectLockTargetCandidateIndex(
                    SimulationDefinition.DefaultSeed,
                    0xBBAA,
                    42,
                    rosterCount,
                    sourceIndex,
                    BehaviorRules.MaximumLockCandidatesPerAttempt),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void CreateTrackingMotion_AimsAtTheTargetAtFullBaseSpeed()
    {
        var motion = BehaviorRules.CreateTrackingMotion(
            new PositionSnapshot(10f, 20f, 30f),
            new PositionSnapshot(10f, 23f, 34f),
            new MotionSnapshot(1f, 0f, 0f, 10f));

        Assert.That(motion, Is.EqualTo(new MotionSnapshot(0f, 0.6f, 0.8f, 50f)));
    }

    [Test]
    public void CreateTrackingMotion_WhenPositionsCoincide_PreservesThePriorDirection()
    {
        var motion = BehaviorRules.CreateTrackingMotion(
            new PositionSnapshot(10f, 20f, 30f),
            new PositionSnapshot(10f, 20f, 30f),
            new MotionSnapshot(0.6f, 0.8f, 0f, 10f));

        Assert.That(motion, Is.EqualTo(new MotionSnapshot(0.6f, 0.8f, 0f, 50f)));
    }
}
