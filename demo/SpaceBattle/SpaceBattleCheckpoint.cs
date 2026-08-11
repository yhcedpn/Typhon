using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle;

internal readonly record struct PauseRunCheckpointSnapshot(
    ulong CompletedTicks,
    uint AliveShipCount);

internal static class SpaceBattleCheckpoint
{
    public static bool TryReadRun(
        Transaction transaction,
        out PauseRunCheckpointSnapshot checkpoint)
    {
        var runEntities = transaction.Query<SimulationRunEntity>().Execute();
        if (runEntities.Count != 1)
        {
            checkpoint = default;
            return false;
        }

        ref readonly PauseRunCheckpointComponent data = ref transaction
            .Open(runEntities.Single())
            .Read(SimulationRunEntity.PauseCheckpoint);
        if (data.IsValid == 0)
        {
            checkpoint = default;
            return false;
        }

        checkpoint = new PauseRunCheckpointSnapshot(data.CompletedTicks, data.AliveShipCount);
        return true;
    }

    public static void Persist(
        DatabaseEngine engine,
        EntityId runEntityId,
        ulong completedTicks)
    {
        using var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate);
        EntityRef runEntity = transaction.OpenMut(runEntityId);
        uint aliveShipCount = 0;
        foreach (EntityId shipId in transaction.Query<Ship>().Execute())
        {
            aliveShipCount++;
            EntityRef ship = transaction.OpenMut(shipId);
            ship.Write(Ship.PauseCheckpoint) = CaptureShip(ship);
        }

        runEntity.Write(SimulationRunEntity.PauseCheckpoint) = new PauseRunCheckpointComponent
        {
            CompletedTicks = completedTicks,
            AliveShipCount = aliveShipCount,
            IsValid = 1,
        };

        foreach (EntityId targetLockId in transaction.Query<TargetLock>().Execute())
        {
            EntityRef targetLock = transaction.OpenMut(targetLockId);
            ref readonly TargetLockComponent data = ref targetLock.Read(TargetLock.Data);
            EntityId owner = data.Owner;
            EntityId target = data.Target;
            targetLock.Write(TargetLock.PauseCheckpoint) = new PauseTargetLockCheckpointComponent
            {
                IsValid = 1,
                OwnerEntityKey = owner.EntityKey,
                TargetEntityKey = target.EntityKey,
                TicksRemaining = data.TicksRemaining,
                Status = data.Status,
            };
        }

        transaction.Commit();
    }

    public static void Restore(
        DatabaseEngine engine,
        EntityId runEntityId,
        ulong completedTicks)
    {
        using var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate);
        ref readonly PauseRunCheckpointComponent runCheckpoint = ref transaction
            .Open(runEntityId)
            .Read(SimulationRunEntity.PauseCheckpoint);
        if (runCheckpoint.IsValid == 0 || runCheckpoint.CompletedTicks != completedTicks)
        {
            transaction.Commit();
            return;
        }

        var shipIds = transaction.Query<Ship>().Execute();
        var shipsByEntityKey = shipIds.ToDictionary(static entityId => entityId.EntityKey);
        foreach (EntityId shipId in shipIds)
        {
            EntityRef ship = transaction.OpenMut(shipId);
            ref readonly PauseShipCheckpointComponent checkpoint = ref ship.Read(Ship.PauseCheckpoint);
            if (checkpoint.IsValid == 0)
            {
                throw new InvalidOperationException($"舰船 {shipId.EntityKey} 缺少暂停检查点。");
            }

            ship.Write(Ship.Position) = new PositionComponent
            {
                X = checkpoint.PositionX,
                Y = checkpoint.PositionY,
                Z = checkpoint.PositionZ,
            };
            ship.Write(Ship.SpatialBounds) = new SpatialBoundsComponent
            {
                Bounds = new AABB3F
                {
                    MinX = checkpoint.BoundsMinX,
                    MinY = checkpoint.BoundsMinY,
                    MinZ = checkpoint.BoundsMinZ,
                    MaxX = checkpoint.BoundsMaxX,
                    MaxY = checkpoint.BoundsMaxY,
                    MaxZ = checkpoint.BoundsMaxZ,
                },
            };
            ship.Write(Ship.Motion) = new MotionComponent
            {
                DirectionX = checkpoint.DirectionX,
                DirectionY = checkpoint.DirectionY,
                DirectionZ = checkpoint.DirectionZ,
                Speed = checkpoint.Speed,
            };
            ship.Write(Ship.Health).Current = checkpoint.Health;
            ship.Write(Ship.Behavior) = new BehaviorComponent
            {
                DecisionOrdinal = checkpoint.DecisionOrdinal,
                ModeTicksRemaining = checkpoint.ModeTicksRemaining,
                Mode = checkpoint.Mode,
            };
            ref TrackingComponent tracking = ref ship.Write(Ship.Tracking);
            tracking.Target = checkpoint.TrackingTargetEntityKey == 0 ||
                !shipsByEntityKey.TryGetValue(checkpoint.TrackingTargetEntityKey, out EntityId targetId)
                    ? EntityLink<Ship>.Null
                    : targetId;
            tracking.TrackingTicksRemaining = checkpoint.TrackingTicksRemaining;

            RestoreEnabledComponent(
                ref ship,
                Ship.Weapon,
                checkpoint.WeaponEnabled != 0,
                new WeaponComponent { CooldownTicksRemaining = checkpoint.CooldownTicksRemaining });
            RestoreEnabledComponent(
                ref ship,
                Ship.Afterburner,
                checkpoint.AfterburnerEnabled != 0,
                new AfterburnerComponent { ActivatedTick = checkpoint.AfterburnerActivatedTick });
        }

        foreach (EntityId targetLockId in transaction.Query<TargetLock>().Execute())
        {
            EntityRef targetLock = transaction.OpenMut(targetLockId);
            ref readonly PauseTargetLockCheckpointComponent checkpoint = ref targetLock
                .Read(TargetLock.PauseCheckpoint);
            if (checkpoint.IsValid == 0)
            {
                throw new InvalidOperationException($"目标锁 {targetLockId.EntityKey} 缺少暂停检查点。");
            }

            if (!shipsByEntityKey.TryGetValue(checkpoint.OwnerEntityKey, out EntityId owner) ||
                !shipsByEntityKey.TryGetValue(checkpoint.TargetEntityKey, out EntityId target))
            {
                throw new InvalidOperationException($"目标锁 {targetLockId.EntityKey} 的端点不在暂停检查点中。");
            }

            targetLock.Write(TargetLock.Data) = new TargetLockComponent
            {
                Owner = owner,
                Target = target,
                TicksRemaining = checkpoint.TicksRemaining,
                Status = checkpoint.Status,
            };
        }

        transaction.Commit();
    }

    public static List<ShipSnapshot> ReadShipSnapshots(Transaction transaction)
    {
        var snapshots = new List<ShipSnapshot>();
        foreach (EntityId shipId in transaction.Query<Ship>().Execute())
        {
            EntityRef ship = transaction.Open(shipId);
            ref readonly PauseShipCheckpointComponent checkpoint = ref ship
                .Read(Ship.PauseCheckpoint);
            if (checkpoint.IsValid == 0)
            {
                throw new InvalidOperationException($"舰船 {shipId.EntityKey} 缺少暂停检查点。");
            }

            snapshots.Add(new ShipSnapshot(
                shipId.EntityKey,
                new PositionSnapshot(checkpoint.PositionX, checkpoint.PositionY, checkpoint.PositionZ),
                new SpatialBoundsSnapshot(
                    checkpoint.BoundsMinX,
                    checkpoint.BoundsMinY,
                    checkpoint.BoundsMinZ,
                    checkpoint.BoundsMaxX,
                    checkpoint.BoundsMaxY,
                    checkpoint.BoundsMaxZ),
                new MotionSnapshot(
                    checkpoint.DirectionX,
                    checkpoint.DirectionY,
                    checkpoint.DirectionZ,
                    checkpoint.Speed),
                checkpoint.Health,
                (BehaviorMode)checkpoint.Mode,
                checkpoint.ModeTicksRemaining,
                checkpoint.TrackingTargetEntityKey == 0,
                checkpoint.WeaponEnabled != 0,
                checkpoint.AfterburnerEnabled != 0));
        }

        snapshots.Sort(static (left, right) => left.EntityKey.CompareTo(right.EntityKey));
        return snapshots;
    }

    public static List<TargetLockSnapshot> ReadTargetLockSnapshots(Transaction transaction)
    {
        var snapshots = new List<TargetLockSnapshot>();
        foreach (EntityId targetLockId in transaction.Query<TargetLock>().Execute())
        {
            EntityRef targetLock = transaction.Open(targetLockId);
            ref readonly PauseTargetLockCheckpointComponent checkpoint = ref targetLock
                .Read(TargetLock.PauseCheckpoint);
            if (checkpoint.IsValid == 0)
            {
                throw new InvalidOperationException($"目标锁 {targetLockId.EntityKey} 缺少暂停检查点。");
            }

            snapshots.Add(new TargetLockSnapshot(
                targetLockId.EntityKey,
                checkpoint.OwnerEntityKey,
                checkpoint.TargetEntityKey,
                (TargetLockStatus)checkpoint.Status,
                checkpoint.TicksRemaining));
        }

        snapshots.Sort(static (left, right) => left.EntityKey.CompareTo(right.EntityKey));
        return snapshots;
    }

    private static PauseShipCheckpointComponent CaptureShip(EntityRef ship)
    {
        ref readonly PositionComponent position = ref ship.Read(Ship.Position);
        ref readonly SpatialBoundsComponent bounds = ref ship.Read(Ship.SpatialBounds);
        ref readonly MotionComponent motion = ref ship.Read(Ship.Motion);
        ref readonly HealthComponent health = ref ship.Read(Ship.Health);
        ref readonly BehaviorComponent behavior = ref ship.Read(Ship.Behavior);
        ref readonly TrackingComponent tracking = ref ship.Read(Ship.Tracking);
        WeaponComponent weapon = ship.IsEnabled(Ship.Weapon)
            ? ship.Read(Ship.Weapon)
            : default;
        AfterburnerComponent afterburner = ship.IsEnabled(Ship.Afterburner)
            ? ship.Read(Ship.Afterburner)
            : default;
        EntityId trackingTarget = tracking.Target;

        return new PauseShipCheckpointComponent
        {
            IsValid = 1,
            PositionX = position.X,
            PositionY = position.Y,
            PositionZ = position.Z,
            BoundsMinX = bounds.Bounds.MinX,
            BoundsMinY = bounds.Bounds.MinY,
            BoundsMinZ = bounds.Bounds.MinZ,
            BoundsMaxX = bounds.Bounds.MaxX,
            BoundsMaxY = bounds.Bounds.MaxY,
            BoundsMaxZ = bounds.Bounds.MaxZ,
            DirectionX = motion.DirectionX,
            DirectionY = motion.DirectionY,
            DirectionZ = motion.DirectionZ,
            Speed = motion.Speed,
            Health = health.Current,
            DecisionOrdinal = behavior.DecisionOrdinal,
            ModeTicksRemaining = behavior.ModeTicksRemaining,
            Mode = behavior.Mode,
            TrackingTargetEntityKey = trackingTarget.IsNull ? 0 : trackingTarget.EntityKey,
            TrackingTicksRemaining = tracking.TrackingTicksRemaining,
            CooldownTicksRemaining = weapon.CooldownTicksRemaining,
            AfterburnerActivatedTick = afterburner.ActivatedTick,
            WeaponEnabled = ship.IsEnabled(Ship.Weapon) ? (byte)1 : (byte)0,
            AfterburnerEnabled = ship.IsEnabled(Ship.Afterburner) ? (byte)1 : (byte)0,
        };
    }

    private static void RestoreEnabledComponent<T>(
        ref EntityRef ship,
        Comp<T> component,
        bool enabled,
        T value)
        where T : unmanaged
    {
        bool wasEnabled = ship.IsEnabled(component);
        if (enabled && !wasEnabled)
        {
            ship.Enable(component);
        }

        if (enabled || wasEnabled)
        {
            ship.Write(component) = value;
        }

        if (!enabled && wasEnabled)
        {
            ship.Disable(component);
        }
    }
}
