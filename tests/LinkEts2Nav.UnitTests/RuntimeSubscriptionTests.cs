using ETS2LA.Game.SDK;
using ETS2LA.Game.Telemetry;
using ETS2LA.Shared;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class RuntimeSubscriptionTests
{
    [Fact]
    public void SourceCopiesMutablePayloadAndStopsReceivingAfterDispose()
    {
        var clock = new FixedClock();
        var bus = new FakeBus();
        var source = new Ets2LaGameDataSource(bus, clock);
        var payload = new GameTelemetryData { sdkActive = true };
        payload.truckPlacement.coordinate.X = 123;
        payload.truckFloat.routeDistance = 1000;
        payload.truckFloat.speedLimit = 50f / 3.6f;
        bus.Publish(Ets2LaGameDataSource.TelemetryTopic, payload);
        payload.truckPlacement.coordinate.X = 999;
        Assert.Equal(123, source.Capture().Vehicle!.X);
        Assert.InRange(source.Capture().Vehicle!.CurrentSpeedLimitKph!.Value,49.99,50.01);
        bus.Publish(Ets2LaGameDataSource.NavigationTopic, new NavigationData
        {
            entries = [new NavigationEntry { nodeUid = 7, distanceToEnd = 500, timeToEnd = 30 }]
        });
        Assert.Equal(RouteAvailability.Available, source.Capture().RouteAvailability);
        source.Dispose();
        source.Dispose();
        Assert.Equal(0, bus.SubscriptionCount);
        bus.Publish(Ets2LaGameDataSource.TelemetryTopic, payload);
        Assert.Equal(123, source.Capture().Vehicle!.X);
        Assert.InRange(source.Capture().Vehicle!.CurrentSpeedLimitKph!.Value,49.99,50.01);
    }

    [Fact]
    public void SourceCapturesFineToggleInBothDirectionsAfterStableSamples()
    {
        var bus = new FakeBus();
        using var source = new Ets2LaGameDataSource(bus, new FixedClock());
        var payload = new GameTelemetryData { sdkActive = true };
        bus.Publish(Ets2LaGameDataSource.TelemetryTopic, payload);
        payload.specialBool.fined = true;
        payload.gameplayString.fineOffence = "speeding_camera";
        payload.gameplayLongLong.fineAmount = 550;
        bus.Publish(Ets2LaGameDataSource.TelemetryTopic, payload);
        bus.Publish(Ets2LaGameDataSource.TelemetryTopic, payload);
        payload.specialBool.fined = false;
        payload.gameplayString.fineOffence = "avoid_sleeping";
        payload.gameplayLongLong.fineAmount = 150;
        bus.Publish(Ets2LaGameDataSource.TelemetryTopic, payload);
        bus.Publish(Ets2LaGameDataSource.TelemetryTopic, payload);
        var fines = source.Store.DrainFines();
        Assert.Equal(2, fines.Count);
        Assert.True(fines[0].EventFlag);
        Assert.False(fines[1].EventFlag);
        Assert.Equal("speeding_camera", source.Capture().LatestCameraFine!.Offence);
    }

    [Fact]
    public void PartialSubscriptionFailureRemovesFirstSubscription()
    {
        var bus = new FakeBus { RejectNavigation = true };
        Assert.Throws<InvalidOperationException>(() => new Ets2LaGameDataSource(bus));
        Assert.Equal(0, bus.SubscriptionCount);
    }

    [Fact]
    public void AnnouncerDoesNotInitializePlaybackUntilWarning()
    {
        int plays = 0;
        var announcer = new Ets2LaWarningAnnouncer(() => plays++);
        Assert.Equal(0, plays);
        announcer.Announce(new("camera", 300, LinkEts2Nav.Core.WarningDistanceKind.Road));
        Assert.Equal(1, plays);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-11-06T00:00:00Z");
    }

    private sealed class FakeBus : IEventBus
    {
        private readonly Dictionary<string, List<Delegate>> handlers = new();
        public bool RejectNavigation { get; init; }
        public int SubscriptionCount => handlers.Values.Sum(x => x.Count);
        public void Subscribe<T>(string topic, Action<T> handler)
        {
            if (RejectNavigation && topic == Ets2LaGameDataSource.NavigationTopic)
                throw new InvalidOperationException("Test subscription failure");
            if (!handlers.TryGetValue(topic, out var list)) handlers[topic] = list = new();
            list.Add(handler);
        }
        public void Unsubscribe<T>(string topic, Action<T> handler)
        {
            if (handlers.TryGetValue(topic, out var list)) list.Remove(handler);
        }
        public void Publish<T>(string topic, T data)
        {
            if (handlers.TryGetValue(topic, out var list))
                foreach (var handler in list.ToArray()) ((Action<T>)handler)(data);
        }
    }
}
