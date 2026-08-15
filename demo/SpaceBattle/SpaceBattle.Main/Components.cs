using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace SpaceBattle;

[Component("SpaceBattle.Hull", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Hull
{
    [Field]
    [SpatialIndex(32f)]
    public AABB3F Bounds;
}

[Component("SpaceBattle.Motion", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Motion
{
    [Field] public float CurrentHeadingX;
    [Field] public float CurrentHeadingY;
    [Field] public float CurrentHeadingZ;
    [Field] public float TargetHeadingX;
    [Field] public float TargetHeadingY;
    [Field] public float TargetHeadingZ;
    [Field] public float Speed;
    [Field] public float RemainingTurnRadians;
}

[Component("SpaceBattle.Vitals", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Size = 8)]
public struct Vitals
{
    [Field] public uint CurrentHealth;
}

[Component("SpaceBattle.Targeting", 2, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Targeting
{
    [Field] public long TargetRawEntityId;
}

[Component("SpaceBattle.Behavior", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Behavior
{
    [Field] public byte Mode;
    [Field] public byte Phase;
    [Field] public ushort TicksRemaining;
    [Field] public long ModeStartedTick;
}

public enum BehaviorMode : byte
{
    Wandering = 0,
    Tracking = 1,
    Approaching = 2,
    Attacking = 3,
    Turning = 4,
}

public enum BehaviorPhase : byte
{
    Ready = 0,
    Aligning = 1,
    Flying = 2,
}
