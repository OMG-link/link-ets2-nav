using System.Numerics;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
namespace LinkEts2Nav.Map;

/// <summary>Nearby disk plus a narrow corridor along the game's route; never a ten-kilometre disk.</summary>
public sealed class MapScanCoverage
{
    public const float LookaheadMeters = 10000;
    private const float CorridorRadius = 100;
    public Vector3 Position { get; }
    public float NearbyRadius { get; }
    public IReadOnlyList<(Vector3 From, Vector3 To)> RouteSegments { get; }
    public IReadOnlyList<SectorCoordinate> Sectors { get; }
    private MapScanCoverage(Vector3 position, float radius, List<(Vector3 From, Vector3 To)> segments)
    {
        Position = position; NearbyRadius = radius; RouteSegments = segments;
        var sectors = NearbyCameraScanner.SectorsNear(position, radius + 100).ToHashSet();
        foreach (var (from, to) in segments)
        {
            // The halo and <=100 m sampling cover every crossed cell, including corners.
            int steps = Math.Max(1, (int)Math.Ceiling(Vector3.Distance(from, to) / 100));
            for (int i = 0; i <= steps; i++)
                sectors.UnionWith(NearbyCameraScanner.SectorsNear(Vector3.Lerp(from, to, (float)i / steps), CorridorRadius + 100));
        }
        Sectors = sectors.OrderBy(s => s.X).ThenBy(s => s.Z).ToArray();
    }
    public static MapScanCoverage Create(Vector3 position, float radius, TruckLib.ScsMap.Map? host,
        RouteSnapshot? route, double remaining)
    {
        var segments = new List<(Vector3, Vector3)>();
        if (host is null || route?.Availability != RouteAvailability.Available || !double.IsFinite(remaining) || remaining <= 0)
            return new(position, radius, segments);
        var nodes = route.Nodes.OrderByDescending(n => n.DistanceToEndMeters).ToArray();
        (INode Node, double Remaining)? previous = null;
        double covered = 0;
        bool started = false;
        foreach (var node in nodes)
        {
            if (!host.Nodes.TryGetValue(node.Uid, out var resolved)) continue;
            var point = resolved.Position;
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) continue;
            if (node.DistanceToEndMeters > remaining)
            { previous = (resolved, node.DistanceToEndMeters); continue; }
            // Official distances locate the current edge, but contain map scale. Never
            // subtract the horizon from them to select a world-space distance.
            var points = previous is { } prior ? Connection(prior.Node, resolved) : [position, point];
            if (!started)
            {
                // Project the actual position onto the first edge, retaining only its forward suffix.
                int bestSegment = 1; float bestT = 0; double bestError = double.PositiveInfinity;
                for (int i = 1; i < points.Length; i++)
                {
                    var delta = points[i] - points[i-1];
                    float t = delta.LengthSquared() > 0 ? Math.Clamp(Vector3.Dot(position-points[i-1],delta)/delta.LengthSquared(),0,1) : 0;
                    double error = Vector3.DistanceSquared(position,points[i-1]+t*delta);
                    if (error < bestError) { bestError=error; bestSegment=i; bestT=t; }
                }
                points = new[]{Vector3.Lerp(points[bestSegment-1],points[bestSegment],bestT)}.Concat(points.Skip(bestSegment)).ToArray();
                started = true;
            }
            for (int i=1;i<points.Length;i++)
            {
                double length=Vector3.Distance(points[i-1],points[i]);
                if(length <= .01) continue;
                double take=Math.Min(length,LookaheadMeters-covered);
                segments.Add((points[i-1],Vector3.Lerp(points[i-1],points[i],(float)(take/length))));
                covered+=take;
                if(covered>=LookaheadMeters) return new(position,radius,segments);
            }
            previous=(resolved,node.DistanceToEndMeters);
        }
        return new(position, radius, segments);
    }
    private static Vector3[] Connection(INode from, INode to)
    {
        // Reuse host-owned road geometry without re-reading map files or indexing the whole map.
        var node=from as Node;
        var road=new[]{node?.ForwardItem,node?.BackwardItem}.OfType<Road>().FirstOrDefault(r=>
            (r.Node?.Uid==from.Uid && r.ForwardNode?.Uid==to.Uid)
            || (r.Node?.Uid==to.Uid && r.ForwardNode?.Uid==from.Uid));
        if(road is null || !float.IsFinite(road.Length) || road.Length<=0) return [from.Position,to.Position];
        int steps=Math.Clamp((int)Math.Ceiling(road.Length/100),1,1000);
        var points=Enumerable.Range(0,steps+1).Select(i=>road.InterpolateCurve((float)i/steps).Position).ToArray();
        if(road.Node.Uid!=from.Uid) Array.Reverse(points);
        return points;
    }

    public bool Contains(Vector3 point, float padding = 0)
    {
        static Vector2 Xz(Vector3 p) => new(p.X, p.Z);
        var p = Xz(point);
        if (Vector2.Distance(p, Xz(Position)) <= NearbyRadius + padding) return true;
        foreach (var (from, to) in RouteSegments)
        {
            var a = Xz(from); var delta = Xz(to) - a;
            float t = delta.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(p - a, delta) / delta.LengthSquared(), 0, 1) : 0;
            if (Vector2.Distance(p, a + t * delta) <= CorridorRadius + padding) return true;
        }
        return false;
    }
}
