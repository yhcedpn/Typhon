using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle;

[Component("SpaceBattle.SimulationRun", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SimulationRunComponent
{
    [Field] public ulong Seed;
    [Field] public ulong CompletedTicks;
    [Field] public uint RulesetVersion;
    [Field] public uint InitialShipCount;
    [Field] public uint AliveShipCount;
}

[Component("SpaceBattle.SimulationRunState", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct SimulationRunStateComponent
{
    [Field] public byte Status;
    [Field] public uint ProcessSegment;
}

[Component("SpaceBattle.Position", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PositionComponent
{
    [Field] public float X;
    [Field] public float Y;
    [Field] public float Z;
}

[Component("SpaceBattle.SpatialBounds", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SpatialBoundsComponent
{
    [Field]
    [SpatialIndex(20f)]
    public AABB3F Bounds;
}

[Component("SpaceBattle.Motion", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct MotionComponent
{
    [Field] public float DirectionX;
    [Field] public float DirectionY;
    [Field] public float DirectionZ;
    [Field] public float Speed;
}

[Component("SpaceBattle.Health", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct HealthComponent
{
    [Field] public uint Current;
}

[Component("SpaceBattle.Behavior", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct BehaviorComponent
{
    [Field] public ulong DecisionOrdinal;
    [Field] public ushort ModeTicksRemaining;
    [Field] public byte Mode;
}

[Component("SpaceBattle.Tracking", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct TrackingComponent
{
    [Field] public EntityLink<Ship> Target;
    [Field] public ushort TrackingTicksRemaining;
}

[Component("SpaceBattle.Weapon", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct WeaponComponent
{
    [Field] public ushort CooldownTicksRemaining;
}

[Component("SpaceBattle.Afterburner", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct AfterburnerComponent
{
    [Field] public ulong ActivatedTick;
}
