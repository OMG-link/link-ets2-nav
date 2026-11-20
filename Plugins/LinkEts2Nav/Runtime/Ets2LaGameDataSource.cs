using System.Runtime.ExceptionServices;
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
    private ExceptionDispatchInfo? navigationInputFailure;
    public GameSnapshotStore Store { get; } = new();

    public Ets2LaGameDataSource(IEventBus events, TimeProvider? clock = null)
    {
        this.events = events;
        this.clock = clock ?? TimeProvider.System;
        events.Subscribe<GameTelemetryData>(TelemetryTopic, OnTelemetry);
        bool navigationRegistered = false, complete = false;
        try
        {
            events.Subscribe<NavigationData>(NavigationTopic, OnNavigation);
            navigationRegistered = true;
            events.Subscribe<SemaphoreData>(TrafficSignalTopic, OnTrafficSignals);
            complete = true;
        }
        finally
        {
            // Registration is transactional. Cleanup does not handle or replace
            // the subscription exception; it continues with its original stack.
            if (!complete)
            {
                if (navigationRegistered) events.Unsubscribe<NavigationData>(NavigationTopic, OnNavigation);
                events.Unsubscribe<GameTelemetryData>(TelemetryTopic, OnTelemetry);
            }
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
        // Event unsubscription cannot retract an already dispatched callback.
        // Store serializes its updates; do not hold the source lock while its
        // route-change subscribers run.
        lock (gate) { if (disposed) return; }
        RouteSnapshot route;
        try
        {
            route = RouteSnapshot.Parse(data.entries.Select(e =>
                new RouteNode(e.nodeUid, e.distanceToEnd, e.timeToEnd)), clock.GetUtcNow());
        }
        catch (InvalidRouteOrderException ex)
        {
            // The host catches subscriber exceptions. Deliver this error to the
            // plugin worker on capture, even when another payload arrives first.
            lock (gate) navigationInputFailure ??= ExceptionDispatchInfo.Capture(ex);
            return;
        }
        Store.UpdateRoute(route);
    }

    public GameDataSnapshot Capture()
    {
        lock (gate) navigationInputFailure?.Throw();
        return Store.Capture(clock.GetUtcNow());
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        events.Unsubscribe<GameTelemetryData>(TelemetryTopic, OnTelemetry);
        events.Unsubscribe<NavigationData>(NavigationTopic, OnNavigation);
        events.Unsubscribe<SemaphoreData>(TrafficSignalTopic, OnTrafficSignals);
    }
}
