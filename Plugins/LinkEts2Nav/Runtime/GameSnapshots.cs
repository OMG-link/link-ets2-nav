using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LinkEts2Nav.Runtime;

public enum RouteAvailability { Unknown, None, Available, Invalid, Stale }

public sealed record VehicleSnapshot(
    double X, double Y, double Z, double HeadingTurns, double SpeedMetersPerSecond,
    double NavigationDistanceRemainingMeters, bool SdkActive, bool Paused,
    ulong SimulationTime, DateTimeOffset ReceivedAt)
{
    public double? CurrentSpeedLimitKph { get; init; }
    /// <summary>Horizontal world forward vector: SCS north=-Z, west=-X; heading is turns.</summary>
    public System.Numerics.Vector3 ForwardDirection => new(
        (float)-Math.Sin(HeadingTurns * Math.Tau), 0, (float)-Math.Cos(HeadingTurns * Math.Tau));

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z)
        && double.IsFinite(HeadingTurns) && double.IsFinite(SpeedMetersPerSecond)
        && double.IsFinite(NavigationDistanceRemainingMeters);
}

public sealed record RouteNode(ulong Uid, float DistanceToEndMeters, float TimeToEndSeconds);

public sealed record RouteSnapshot(
    IReadOnlyList<RouteNode> Nodes, string Revision, RouteAvailability Availability,
    DateTimeOffset ReceivedAt, bool MayBeTruncated = false)
{
    public static RouteSnapshot Parse(IEnumerable<RouteNode> source, DateTimeOffset receivedAt)
    {
        var nodes = new List<RouteNode>();
        bool padding = false;
        foreach (var node in source)
        {
            if (node.Uid == 0) { padding = true; continue; }
            // A nonzero node after zero padding signals an inconsistent shared-memory payload.
            if (padding || !float.IsFinite(node.DistanceToEndMeters) || node.DistanceToEndMeters < 0
                || !float.IsFinite(node.TimeToEndSeconds) || node.TimeToEndSeconds < 0)
                return new(Array.Empty<RouteNode>(), string.Empty, RouteAvailability.Invalid, receivedAt);
            nodes.Add(node);
        }
        if (nodes.Count == 0)
            return new(Array.Empty<RouteNode>(), string.Empty, RouteAvailability.None, receivedAt);
        var bytes = new byte[nodes.Count * sizeof(ulong)];
        for (int i = 0; i < nodes.Count; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * sizeof(ulong)), nodes[i].Uid);
        return new(nodes.AsReadOnly(), Convert.ToHexString(SHA256.HashData(bytes)),
            RouteAvailability.Available, receivedAt, nodes.Count >= 6000);
    }
}

public enum FineObservationSignal { Toggle, PayloadChange }

/// <summary>Vehicle pose observed with a fine flag; not an exact offence location or camera identity.</summary>
public sealed record FineObservation(string Offence, ulong Amount, VehicleSnapshot Vehicle, DateTimeOffset ObservedAt)
{
    public bool IsSpeedCameraOffence => string.Equals(Offence, "speeding_camera", StringComparison.Ordinal);
    public bool EventFlag { get; init; }
    public long Sequence { get; init; }
    public FineObservationSignal Signal { get; init; }
    public bool IsSignalCertain => Signal == FineObservationSignal.Toggle;
}

public sealed record GameDataSnapshot(VehicleSnapshot? Vehicle, RouteSnapshot Route,
    RouteAvailability RouteAvailability, string SessionId, bool IsVehicleUsable, FineObservation? LatestFine = null,
    FineObservation? LatestCameraFine = null, IReadOnlyList<FineObservation>? FineHistory = null, long DroppedFineCount = 0)
{
    public PositionSignalStatus PositionSignal {get;init;}=PositionSignalStatus.None;
    /// <summary>Paused fresh placement can warm read-only map data, but never authorizes a warning.</summary>
    public bool CanPrepareMap(DateTimeOffset now) => Vehicle is { SdkActive: true, IsFinite: true } pose
        && now >= pose.ReceivedAt && now - pose.ReceivedAt <= TimeSpan.FromSeconds(2)
        && (IsVehicleUsable || pose.Paused);
}

/// <summary>Copies event data into immutable snapshots; stale routes never mean no route.</summary>
public sealed class GameSnapshotStore
{
    private readonly object gate = new();
    private VehicleSnapshot? vehicle;
    private DateTimeOffset lastVehicleProgress;
    private readonly PositionSignalTracker positionSignal=new();
    private readonly FineObservationTracker fines = new();
    private RouteSnapshot route = new(Array.Empty<RouteNode>(), string.Empty, RouteAvailability.Unknown, default);
    private long session;
    private DateTimeOffset? zeroNavigationSince;
    private int zeroNavigationSamples;
    private readonly string instanceId = Guid.NewGuid().ToString("N");

    public void UpdateVehicle(VehicleSnapshot next)
    {
        lock (gate)
        {
            if (vehicle is null || next.SimulationTime != vehicle.SimulationTime
                || next.SdkActive != vehicle.SdkActive || next.Paused != vehicle.Paused)
                lastVehicleProgress = next.ReceivedAt;
            if (vehicle is { } previous)
            {
                double seconds = Math.Max(0, (next.ReceivedAt - previous.ReceivedAt).TotalSeconds);
                double distance = Math.Sqrt(Math.Pow(next.X - previous.X, 2) + Math.Pow(next.Y - previous.Y, 2)
                    + Math.Pow(next.Z - previous.Z, 2));
                bool discontinuity = next.SdkActive != previous.SdkActive
                    || next.SimulationTime < previous.SimulationTime
                    || distance > Math.Max(200, Math.Abs(previous.SpeedMetersPerSecond) * seconds + 100);
                if (discontinuity)
                {
                    session++;
                    zeroNavigationSince = null;
                    zeroNavigationSamples = 0;
                    fines.ResetSession();
                    positionSignal.Reset();
                    route = new(Array.Empty<RouteNode>(), string.Empty, RouteAvailability.Unknown, default);
                }
            }
            if (next.SdkActive && !next.Paused && next.IsFinite && next.NavigationDistanceRemainingMeters == 0)
            {
                zeroNavigationSince ??= next.ReceivedAt;
                if (vehicle is null || next.SimulationTime != vehicle.SimulationTime) zeroNavigationSamples++;
            }
            else { zeroNavigationSince = null; zeroNavigationSamples = 0; }
            positionSignal.Update(vehicle,next);
            vehicle = next;
        }
    }

    public void UpdateRoute(RouteSnapshot next) { lock (gate) route = next; }

    public void ObserveFine(bool fined, string offence, ulong amount, DateTimeOffset observedAt)
    {
        lock (gate)
        {
            bool usablePose = vehicle is { SdkActive: true, Paused: false, IsFinite: true }
                && IsFresh(vehicle.ReceivedAt, observedAt, TimeSpan.FromSeconds(2))
                && IsFresh(lastVehicleProgress, observedAt, TimeSpan.FromSeconds(2));
            fines.Observe(fined, offence, amount, usablePose ? vehicle : null, observedAt);
        }
    }

    /// <summary>Returns committed observations once; session changes do not erase this pending log queue.</summary>
    public IReadOnlyList<FineObservation> DrainFines()
    {
        lock (gate) return fines.Drain();
    }

    public GameDataSnapshot Capture(DateTimeOffset now)
    {
        lock (gate)
        {
            bool usable = vehicle is { SdkActive: true, Paused: false, IsFinite: true }
                && IsFresh(vehicle.ReceivedAt, now, TimeSpan.FromSeconds(2))
                && IsFresh(lastVehicleProgress, now, TimeSpan.FromSeconds(2));
            var availability = route.Availability;
            if (availability != RouteAvailability.Unknown && !IsFresh(route.ReceivedAt, now, TimeSpan.FromSeconds(3)))
                availability = RouteAvailability.Stale;
            if (usable && availability == RouteAvailability.None && vehicle!.NavigationDistanceRemainingMeters > 1)
                availability = RouteAvailability.Invalid;
            if (usable && availability == RouteAvailability.Available && vehicle!.NavigationDistanceRemainingMeters <= 0)
            {
                // The route mmap can keep publishing the last node list after GPS is cleared.
                // Only sustained, progressing telemetry explicitly reporting zero overrides it.
                bool cleared = zeroNavigationSamples >= 3 && zeroNavigationSince is { } since
                    && now >= since && now - since >= TimeSpan.FromSeconds(1);
                availability = cleared ? RouteAvailability.None : RouteAvailability.Invalid;
            }
            return new(vehicle, route, availability, $"{instanceId}:{session}", usable,
                fines.Latest, fines.LatestCamera, fines.GetHistory(), fines.DroppedCount)
                {PositionSignal=positionSignal.Capture(now)};
        }
    }

    private static bool IsFresh(DateTimeOffset received, DateTimeOffset now, TimeSpan maxAge)
        => now >= received && now - received <= maxAge;
}
