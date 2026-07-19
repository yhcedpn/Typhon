namespace SpaceBattle;

public interface ISpaceBattleObservationSink
{
    void Publish(SpaceBattleObservation observation);
}

public abstract record SpaceBattleObservation;

public sealed record InitializationProgress(
    int CreatedShips,
    int TotalShips) : SpaceBattleObservation;

public sealed record InitializationCompleted(
    int ShipCount,
    TimeSpan Duration) : SpaceBattleObservation;
