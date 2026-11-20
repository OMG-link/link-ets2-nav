using ETS2LA.Game.SDK;
using ETS2LA.Game.Telemetry;
using ETS2LA.Shared;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class RuntimeSubscriptionTests
{
    [Fact]
    public void NavigationNotificationsUpdateStableRouteAndInvokeObserversOutsideSourceLock()
    {
        var bus = new FakeBus(); using var source = new Ets2LaGameDataSource(bus, new FixedClock());
        int changed = 0;
        source.Store.RouteChanged += _ => changed++;
        bus.Publish(Ets2LaGameDataSource.NavigationTopic, new NavigationData
        { entries = [new() { nodeUid = 7, distanceToEnd = 100 }, new() { nodeUid = 8 }] });
        bus.Publish(Ets2LaGameDataSource.NavigationTopic, new NavigationData
        { entries = [new() { nodeUid = 8 }] });
        var snapshot = source.Capture();
        Assert.Equal(1, changed); Assert.Equal(1, snapshot.NavigationRoute.CurrentNodeIndex);
        Assert.Equal(new ulong[] { 7, 8 }, snapshot.NavigationRoute.FullRoute.Nodes.Select(n => n.Uid));
        source.Store.RouteChanged += _ => Assert.True(Task.Run(source.Dispose).Wait(TimeSpan.FromSeconds(3)));
        bus.Publish(Ets2LaGameDataSource.NavigationTopic, new NavigationData
        { entries = [new() { nodeUid = 9 }] });
        Assert.Equal(2, changed);
    }

    [Theory]
    [InlineData(0, 100, 200)]
    [InlineData(200, 100, 100)]
    [InlineData(100, 200, 0)]
    public void InvalidRouteOrderThrowsOnPluginCaptureEvenAfterAnotherPayload(float first, float second, float last)
    {
        var bus = new FakeBus();
        using var source = new Ets2LaGameDataSource(bus, new FixedClock());

        void Publish(float a, float b, float c) =>
            bus.Publish(Ets2LaGameDataSource.NavigationTopic, new NavigationData
            {
                entries = [
                    new() { nodeUid = 7, distanceToEnd = a },
                    new() { nodeUid = 8, distanceToEnd = b },
                    new() { nodeUid = 9, distanceToEnd = c },
                ]
            });

        Publish(200, 100, 0);
        var original = source.Capture();
        Publish(first, second, last);
        Assert.Same(original.Route, source.Store.Capture(original.Route.ReceivedAt).Route);
        Publish(300, 150, 0);

        var error = Assert.Throws<InvalidRouteOrderException>(() => source.Capture());
        Assert.Contains("distances must strictly decrease", error.Message);
        Assert.Contains(nameof(RouteSnapshot.Parse), error.StackTrace);
    }

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
    public void TrafficSignalsAreCopiedAndUnsubscribedWithTheSource()
    {
        var bus=new FakeBus();var source=new Ets2LaGameDataSource(bus,new FixedClock());
        var signal=new ETS2LA.Game.SDK.Semaphore{type=SemaphoreType.TRAFFICLIGHT,position=new(10,0,20),
            rotation=new(1,0,0,0),state=(int)SignalPhase.Red,time_remaining=3,id=7};
        bus.Publish(Ets2LaGameDataSource.TrafficSignalTopic,new SemaphoreData{semaphores=[signal]});
        signal.time_remaining=100;signal.position.X=200;
        var copied=Assert.Single(source.Capture().TrafficSignals.Signals);
        Assert.Equal(3,copied.PhaseRemaining);Assert.Equal(10,copied.Position.X);
        Assert.Equal(System.Numerics.Quaternion.Identity,copied.Rotation);
        source.Dispose();Assert.Equal(0,bus.SubscriptionCount);
        bus.Publish(Ets2LaGameDataSource.TrafficSignalTopic,new SemaphoreData{semaphores=[]});
        Assert.Single(source.Capture().TrafficSignals.Signals);
    }

    [Fact]
    public void AThirdSubscriptionFailureRollsBackBothEarlierSubscriptions()
    {
        var bus=new FakeBus{RejectSignals=true};
        Assert.Throws<InvalidOperationException>(()=>new Ets2LaGameDataSource(bus));
        Assert.Equal(0,bus.SubscriptionCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RegistrationCleanupPreservesUnknownExceptionAndItsOriginalThrowSite(bool navigation)
    {
        var original = new SubscriptionTestException();
        var bus = new FakeBus { RejectNavigation = navigation, RejectSignals = !navigation, Failure = original };
        var observed = Assert.Throws<SubscriptionTestException>(() => new Ets2LaGameDataSource(bus));
        Assert.Same(original, observed);
        Assert.Contains(nameof(FakeBus.Subscribe), observed.StackTrace);
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
        public bool RejectSignals {get;init;}
        public Exception? Failure { get; init; }
        public int SubscriptionCount => handlers.Values.Sum(x => x.Count);
        public void Subscribe<T>(string topic, Action<T> handler)
        {
            if (RejectNavigation && topic == Ets2LaGameDataSource.NavigationTopic)
                throw Failure ?? new InvalidOperationException("Test subscription failure");
            if(RejectSignals&&topic==Ets2LaGameDataSource.TrafficSignalTopic)
                throw Failure ?? new InvalidOperationException("Test signal subscription failure");
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
    private sealed class SubscriptionTestException : Exception;
}
