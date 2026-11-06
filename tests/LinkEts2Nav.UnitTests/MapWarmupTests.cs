using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;
public sealed class MapWarmupTests
{
    [Fact]
    public void FreshPausedVehicleCanPrepareMapWithoutBeingEligibleForAlerts()
    {
        var now = DateTimeOffset.UtcNow;
        var pose = new VehicleSnapshot(1, 2, 3, 0, 0, 1000, true, true, 1, now);
        var data = new GameDataSnapshot(pose, RouteSnapshot.Parse([], now), RouteAvailability.None, "session", false);
        Assert.True(data.CanPrepareMap(now));
        Assert.False(data.IsVehicleUsable);
        Assert.False(data.CanPrepareMap(now.AddSeconds(3)));
    }

    [Fact]
    public void FrozenUnpausedVehicleDoesNotQualifyForMapWarmup()
    {
        var now = DateTimeOffset.UtcNow;
        var pose = new VehicleSnapshot(1, 2, 3, 0, 0, 1000, true, false, 1, now);
        var data = new GameDataSnapshot(pose, RouteSnapshot.Parse([], now), RouteAvailability.None, "session", false);
        Assert.False(data.CanPrepareMap(now));
    }
}
