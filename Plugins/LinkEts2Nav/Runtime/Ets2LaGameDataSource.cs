using ETS2LA.Game.SDK;
using ETS2LA.Game.Telemetry;
using ETS2LA.Shared;

namespace LinkEts2Nav.Runtime;

/// <summary>Owns only event subscriptions, never the host's global SDK reader threads.</summary>
public sealed class Ets2LaGameDataSource : IDisposable
{
    public const string TelemetryTopic = "ETS2LA.Telemetry.Data";
    public const string NavigationTopic = "ETS2LA.Game.SDK.Navigation.Data";
    public const string TrafficSignalTopic = "ETS2LA.Game.SDK.Semaphore.Data";
    private readonly IEventBus events;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private bool disposed;
    public GameSnapshotStore Store { get; } = new();

    public Ets2LaGameDataSource(IEventBus events, TimeProvider? clock = null)
    {
        this.events = events;
        this.clock = clock ?? TimeProvider.System;
        events.Subscribe<GameTelemetryData>(TelemetryTopic, OnTelemetry);
        try { events.Subscribe<NavigationData>(NavigationTopic, OnNavigation); }
        catch { events.Unsubscribe<GameTelemetryData>(TelemetryTopic, OnTelemetry); throw; }
        try { events.Subscribe<SemaphoreData>(TrafficSignalTopic, OnTrafficSignals); }
        catch
        {
            events.Unsubscribe<NavigationData>(NavigationTopic, OnNavigation);
            events.Unsubscribe<GameTelemetryData>(TelemetryTopic, OnTelemetry);throw;
        }
    }

    private void OnTrafficSignals(SemaphoreData data)
    {
        lock(gate)
        {
            if(disposed)return;
            Store.UpdateTrafficSignals(TrafficSignalAdapter.Copy(data.semaphores,clock.GetUtcNow()));
        }
    }

    private void OnTelemetry(GameTelemetryData data)
    {
        lock (gate)
        {
            if (disposed) return;
            var position = data.truckPlacement.coordinate;
            var observedAt = clock.GetUtcNow();
            Store.UpdateVehicle(new(position.X, position.Y, position.Z, data.truckPlacement.rotation.X,
                data.truckFloat.speed, data.truckFloat.routeDistance, data.sdkActive, data.paused,
                data.simulatedTime, observedAt)
            {
                CurrentSpeedLimitKph = float.IsFinite(data.truckFloat.speedLimit) && data.truckFloat.speedLimit > 0
                    ? data.truckFloat.speedLimit * 3.6 : null
            },new(data.specialBool.ferry,data.gameplayString.ferrySourceId,data.gameplayString.ferryTargetId,data.gameplayLongLong.ferryPayAmount));
            Store.ObserveFine(data.specialBool.fined, data.gameplayString.fineOffence,
                data.gameplayLongLong.fineAmount, observedAt);
        }
    }

    private void OnNavigation(NavigationData data)
    {
        lock (gate)
        {
            if (disposed) return;
            Store.UpdateRoute(RouteSnapshot.Parse(data.entries.Select(e =>
                new RouteNode(e.nodeUid, e.distanceToEnd, e.timeToEnd)), clock.GetUtcNow()));
        }
    }

    public GameDataSnapshot Capture() => Store.Capture(clock.GetUtcNow());

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        events.Unsubscribe<GameTelemetryData>(TelemetryTopic, OnTelemetry);
        events.Unsubscribe<NavigationData>(NavigationTopic, OnNavigation);
        events.Unsubscribe<SemaphoreData>(TrafficSignalTopic, OnTrafficSignals);
    }
}
