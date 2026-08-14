using System.Numerics;
using Typhon.Engine;

namespace SpaceBattle;

internal static class SpaceBattleDatabase
{
    public static DatabaseEngine Open(SimulationDefinition definition, string databaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseDirectory);
        definition.Validate();

        _ = Ship.Hull;
        return DatabaseEngine.Open(databaseDirectory, options => options
            .Register<Hull>()
            .Register<Motion>()
            .Register<Vitals>()
            .Register<Targeting>()
            .Register<Behavior>()
            .ConfigureSpatialGrid(new SpatialGridConfig(
                Vector2.Zero,
                new Vector2(definition.WorldWidth, definition.WorldHeight),
                definition.SpatialCellSize)));
    }
}
