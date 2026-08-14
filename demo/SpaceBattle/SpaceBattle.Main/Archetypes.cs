using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle;

[Archetype]
public sealed partial class Ship : Archetype<Ship>
{
    public static readonly Comp<Hull> Hull = Register<Hull>();
    public static readonly Comp<Motion> Motion = Register<Motion>();
    public static readonly Comp<Vitals> Vitals = Register<Vitals>();
    public static readonly Comp<Targeting> Targeting = Register<Targeting>();
    public static readonly Comp<Behavior> Behavior = Register<Behavior>();
}
