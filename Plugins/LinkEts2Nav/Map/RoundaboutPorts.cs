using System.Numerics;
namespace LinkEts2Nav.Map;

public sealed record RoundaboutEntryPort(ulong NodeUid,Vector3 Position)
{
    public double Bearing { get; init; }
}
public sealed record RoundaboutExitPort(ulong BoundaryNodeUid,Vector3 Position)
{
    /// <summary>Native starts of exit-only branches; actual mileage is measured on the selected route.</summary>
    public IReadOnlyList<RoundaboutExitBranch> DepartureBranches { get; init; } = [];
    public IReadOnlyList<Vector3> DepartureCandidates=>DepartureBranches.Select(b=>b.Position).ToArray();
    public double Bearing { get; init; }
    public IReadOnlyList<RoundaboutCurveKey> Branches { get; init; } = [];
}

public sealed record RoundaboutExitBranch(RoundaboutCurveKey Curve,Vector3 Position);

/// <summary>Physical directed interfaces ordered around one reference center, independent of ring connectivity.</summary>
public static class RoundaboutPorts
{
    public static (RoundaboutEntryPort[] Entries,RoundaboutExitPort[] Exits,double Length) Build(
        IReadOnlyDictionary<RoundaboutCurveKey,RoundaboutCurve> graph,
        IReadOnlyList<RoundaboutCurveKey> ring,IReadOnlyList<ulong> members,CancellationToken token=default)
    {
        var boundary=RoundaboutBoundary.Build(graph,ring,members);
        double weight=0,centerX=0,centerZ=0,area=0;
        var origin=graph[ring[0]].Points[0];
        // Length weighting avoids bias from uneven sampling densities.
        foreach(var key in ring)
        {
            token.ThrowIfCancellationRequested();
            var points=graph[key].Points;
            for(int i=1;i<points.Count;i++)
            {
                var a=points[i-1];var b=points[i];double length=Vector3.Distance(a,b);
                weight+=length;centerX+=((double)a.X+b.X)*.5*length;centerZ+=((double)a.Z+b.Z)*.5*length;
                area+=(double)(a.X-origin.X)*(b.Z-origin.Z)-(double)(b.X-origin.X)*(a.Z-origin.Z);
            }
        }
        centerX/=weight;centerZ/=weight;double direction=area>=0?1:-1;
        double Bearing(Vector3 p)=>direction*Math.Atan2(p.Z-centerZ,p.X-centerX);
        var departures=RoundaboutExitGeometry.Build(boundary,token);
        return (boundary.Entries.Select(p=>new RoundaboutEntryPort(p.NodeUid,p.Position){Bearing=Bearing(p.Position)}).ToArray(),
            boundary.Exits.Select(p=>new RoundaboutExitPort(p.NodeUid,p.Position)
                {Bearing=Bearing(p.Position),Branches=p.Curves,DepartureBranches=departures[p.NodeUid]}).ToArray(),weight);
    }
}
