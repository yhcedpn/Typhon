using System.Numerics;

namespace SpaceBattle;

/// <summary>集中行为模式推进、锁定转换和各模式的航向移动规则。</summary>
internal sealed class SpaceBattleBehaviorModes
{
    private readonly SpaceBattleSimulationState _state;

    public SpaceBattleBehaviorModes(SpaceBattleSimulationState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public void Advance(
        long tickNumber,
        in ShipSnapshot frame,
        ref Motion motion,
        ref Targeting targeting,
        ref Behavior behavior)
    {
        switch ((BehaviorMode)frame.Behavior.Mode)
        {
            case BehaviorMode.Wandering:
                AdvanceWandering(
                    tickNumber,
                    frame.EntityKey,
                    ref motion,
                    ref behavior,
                    (BehaviorPhase)frame.Behavior.Phase);
                return;
            case BehaviorMode.Tracking:
                return;
            case BehaviorMode.Approaching:
            case BehaviorMode.Attacking:
                AdvanceTargetedMode(tickNumber, frame, ref motion, ref targeting, ref behavior);
                return;
            case BehaviorMode.Turning:
                AdvanceTurning(
                    tickNumber,
                    frame.EntityKey,
                    ref motion,
                    ref behavior,
                    (BehaviorPhase)frame.Behavior.Phase);
                return;
            default:
                throw UnknownMode(frame.Behavior.Mode, frame.EntityKey);
        }
    }

    public static void ApplyAcquiredTarget(
        long tickNumber,
        in TargetingResult result,
        ref Motion motion,
        ref Targeting targeting,
        ref Behavior behavior)
    {
        if (result.EntityId.IsNull)
        {
            // 没有候选时保留原速度和航向，下一逻辑帧继续重试。
            targeting.TargetRawEntityId = 0;
            return;
        }

        targeting.TargetRawEntityId = SpaceBattleTargeting.PackRaw(result.EntityId);
        behavior.Mode = (byte)(result.DistanceSquared <= SpaceBattleCombat.WeaponRange * SpaceBattleCombat.WeaponRange
            ? BehaviorMode.Attacking
            : BehaviorMode.Approaching);
        behavior.Phase = (byte)BehaviorPhase.Ready;
        behavior.TicksRemaining = 0;
        behavior.ModeStartedTick = tickNumber + 1;
        motion.Speed = SpaceBattleCombat.AttackSpeed;
    }

    public bool TryMove(
        in ShipSnapshot frame,
        in Motion currentMotion,
        bool pendingReap,
        out Hull nextHull,
        out Motion nextMotion)
    {
        nextHull = frame.Hull;
        nextMotion = currentMotion;
        if (frame.Vitals.CurrentHealth == 0 || pendingReap)
        {
            return false;
        }

        var mode = (BehaviorMode)frame.Behavior.Mode;
        var phase = (BehaviorPhase)frame.Behavior.Phase;
        switch (mode)
        {
            case BehaviorMode.Wandering when phase == BehaviorPhase.Aligning:
                nextMotion = TurnWandering(frame);
                return true;
            case BehaviorMode.Turning when phase == BehaviorPhase.Aligning:
                nextMotion = TurnEvasive(frame, currentMotion);
                return true;
            case BehaviorMode.Wandering:
            case BehaviorMode.Tracking:
            case BehaviorMode.Approaching:
            case BehaviorMode.Attacking:
            case BehaviorMode.Turning:
                break;
            default:
                throw UnknownMode(frame.Behavior.Mode, frame.EntityKey);
        }

        var isTargeted = mode is BehaviorMode.Approaching or BehaviorMode.Attacking;
        var shouldMove = mode == BehaviorMode.Tracking ||
                         isTargeted ||
                         (mode == BehaviorMode.Wandering &&
                          phase == BehaviorPhase.Flying &&
                          frame.Behavior.TicksRemaining > 0) ||
                         (mode == BehaviorMode.Turning &&
                          phase == BehaviorPhase.Flying &&
                          frame.Behavior.TicksRemaining > 0);
        if (!shouldMove)
        {
            return false;
        }

        var heading = CurrentHeading(currentMotion);
        if (isTargeted && SpaceBattleTargeting.TryReadTarget(_state, frame, out var targetFrame, out _))
        {
            var direction = SpaceBattleTargeting.PositionOf(targetFrame) - SpaceBattleTargeting.PositionOf(frame);
            if (direction.LengthSquared() > 1e-12f)
            {
                direction = Vector3.Normalize(direction);
            }

            var turned = SpaceBattleMath.TurnTowards(
                heading,
                direction,
                TurnStep,
                out var remainingRadians);
            heading = turned;
            SetCurrentHeading(ref nextMotion, turned);
            nextMotion.RemainingTurnRadians = remainingRadians;
        }

        var movementSpeed = mode == BehaviorMode.Tracking
            ? frame.Motion.Speed
            : currentMotion.Speed;
        var bounds = SpaceBattleMath.MoveBounds(
            frame.Hull.Bounds,
            heading,
            movementSpeed,
            _state.FixedDeltaSeconds,
            _state.WorldWidth,
            _state.WorldHeight,
            _state.WorldDepth,
            out var resultingHeading);
        nextHull = new Hull { Bounds = bounds };
        SetCurrentHeading(ref nextMotion, resultingHeading);
        SetTargetHeading(ref nextMotion, resultingHeading);
        nextMotion.RemainingTurnRadians = isTargeted
            ? nextMotion.RemainingTurnRadians
            : 0f;
        return true;
    }

    private float TurnStep => SpaceBattleMath.MaximumTurnRadiansPerSecond * _state.FixedDeltaSeconds;

    private void AdvanceTargetedMode(
        long tickNumber,
        in ShipSnapshot source,
        ref Motion motion,
        ref Targeting targeting,
        ref Behavior behavior)
    {
        if (!SpaceBattleTargeting.TryReadTarget(_state, source, out _, out var distanceSquared))
        {
            // 锁定关系失效的这一帧清除锁定，Movement 仍用失效前的速度完成一次移动。
            targeting.TargetRawEntityId = 0;
            behavior.Mode = (byte)BehaviorMode.Turning;
            behavior.Phase = (byte)BehaviorPhase.Ready;
            behavior.TicksRemaining = 0;
            behavior.ModeStartedTick = tickNumber + 1;
            motion.RemainingTurnRadians = 0f;
            return;
        }

        motion.Speed = SpaceBattleCombat.AttackSpeed;
        if ((BehaviorMode)source.Behavior.Mode == BehaviorMode.Approaching &&
            distanceSquared <= SpaceBattleCombat.WeaponRange * SpaceBattleCombat.WeaponRange)
        {
            behavior.Mode = (byte)BehaviorMode.Attacking;
            behavior.ModeStartedTick = tickNumber + 1;
        }

        behavior.Phase = (byte)BehaviorPhase.Ready;
        behavior.TicksRemaining = 0;
    }

    private void AdvanceWandering(
        long tickNumber,
        long entityKey,
        ref Motion motion,
        ref Behavior behavior,
        BehaviorPhase phase)
    {
        switch (phase)
        {
            case BehaviorPhase.Ready:
            {
                var hasCurrentHeading = motion.CurrentHeadingX != 0f ||
                                        motion.CurrentHeadingY != 0f ||
                                        motion.CurrentHeadingZ != 0f;
                var purpose = hasCurrentHeading
                    ? SpaceBattleRandomPurpose.WanderHeading
                    : SpaceBattleRandomPurpose.InitialWanderHeading;
                var target = SpaceBattleMath.RandomDirection(_state.Seed, entityKey, behavior.ModeStartedTick, purpose);
                SetTargetHeading(ref motion, target);
                motion.Speed = SpaceBattleMath.RandomWanderSpeed(_state.Seed, entityKey, behavior.ModeStartedTick);

                if (!hasCurrentHeading)
                {
                    SetCurrentHeading(ref motion, target);
                    motion.RemainingTurnRadians = 0f;
                    behavior.Phase = (byte)BehaviorPhase.Flying;
                    behavior.TicksRemaining = SpaceBattleMath.WanderFlightTicks;
                    break;
                }

                var angle = SpaceBattleMath.AngleBetween(CurrentHeading(motion), target);
                motion.RemainingTurnRadians = angle;
                if (angle <= TurnStep)
                {
                    SetCurrentHeading(ref motion, target);
                    motion.RemainingTurnRadians = 0f;
                    behavior.Phase = (byte)BehaviorPhase.Flying;
                    behavior.TicksRemaining = SpaceBattleMath.WanderFlightTicks;
                }
                else
                {
                    behavior.Phase = (byte)BehaviorPhase.Aligning;
                    behavior.TicksRemaining = 0;
                }

                break;
            }
            case BehaviorPhase.Aligning:
            {
                var angle = SpaceBattleMath.AngleBetween(CurrentHeading(motion), TargetHeading(motion));
                motion.RemainingTurnRadians = angle;
                if (angle <= TurnStep)
                {
                    behavior.Phase = (byte)BehaviorPhase.Flying;
                    behavior.TicksRemaining = SpaceBattleMath.WanderFlightTicks;
                }

                break;
            }
            case BehaviorPhase.Flying:
                if (behavior.TicksRemaining == 0)
                {
                    behavior.Mode = (byte)BehaviorMode.Tracking;
                    behavior.Phase = (byte)BehaviorPhase.Ready;
                    behavior.ModeStartedTick = tickNumber + 1;
                }
                else
                {
                    behavior.TicksRemaining--;
                }

                break;
        }
    }

    private void AdvanceTurning(
        long tickNumber,
        long entityKey,
        ref Motion motion,
        ref Behavior behavior,
        BehaviorPhase phase)
    {
        switch (phase)
        {
            case BehaviorPhase.Ready:
            {
                var target = SpaceBattleMath.RandomTurnTarget(
                    _state.Seed,
                    entityKey,
                    behavior.ModeStartedTick,
                    CurrentHeading(motion),
                    out var turnRadians);
                SetTargetHeading(ref motion, target);
                motion.RemainingTurnRadians = turnRadians;
                motion.Speed = 0f;
                behavior.Phase = (byte)BehaviorPhase.Aligning;
                behavior.TicksRemaining = 0;
                break;
            }
            case BehaviorPhase.Aligning:
                if (!float.IsFinite(motion.RemainingTurnRadians) || motion.RemainingTurnRadians <= TurnStep)
                {
                    motion.RemainingTurnRadians = MathF.Max(0f, motion.RemainingTurnRadians);
                    motion.Speed = SpaceBattleMath.EvasiveSpeed;
                    behavior.Phase = (byte)BehaviorPhase.Flying;
                    behavior.TicksRemaining = SpaceBattleMath.EvasiveFlightTicks;
                }

                break;
            case BehaviorPhase.Flying:
                if (behavior.TicksRemaining == 0)
                {
                    behavior.Mode = (byte)BehaviorMode.Wandering;
                    behavior.Phase = (byte)BehaviorPhase.Ready;
                    behavior.ModeStartedTick = tickNumber + 1;
                }
                else
                {
                    behavior.TicksRemaining--;
                }

                break;
        }
    }

    private Motion TurnWandering(in ShipSnapshot frame)
    {
        var motion = frame.Motion;
        var turned = SpaceBattleMath.TurnTowards(
            CurrentHeading(frame.Motion),
            TargetHeading(frame.Motion),
            TurnStep,
            out var remainingRadians);
        SetCurrentHeading(ref motion, turned);
        motion.RemainingTurnRadians = remainingRadians;
        return motion;
    }

    private Motion TurnEvasive(in ShipSnapshot frame, in Motion currentMotion)
    {
        var motion = currentMotion;
        var turned = SpaceBattleMath.TurnAlongGreatCircle(
            CurrentHeading(frame.Motion),
            TargetHeading(frame.Motion),
            frame.Motion.RemainingTurnRadians,
            TurnStep,
            out var remainingRadians);
        SetCurrentHeading(ref motion, turned);
        motion.RemainingTurnRadians = remainingRadians;
        return motion;
    }

    private static Vector3 CurrentHeading(in Motion motion) =>
        new(motion.CurrentHeadingX, motion.CurrentHeadingY, motion.CurrentHeadingZ);

    private static Vector3 TargetHeading(in Motion motion) =>
        new(motion.TargetHeadingX, motion.TargetHeadingY, motion.TargetHeadingZ);

    private static void SetCurrentHeading(ref Motion motion, Vector3 heading)
    {
        motion.CurrentHeadingX = heading.X;
        motion.CurrentHeadingY = heading.Y;
        motion.CurrentHeadingZ = heading.Z;
    }

    private static void SetTargetHeading(ref Motion motion, Vector3 heading)
    {
        motion.TargetHeadingX = heading.X;
        motion.TargetHeadingY = heading.Y;
        motion.TargetHeadingZ = heading.Z;
    }

    private static InvalidOperationException UnknownMode(byte mode, long entityKey) =>
        new($"SpaceBattle 飞船 {entityKey} 使用未知行为模式 {mode}。");
}
