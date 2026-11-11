using System.Numerics;
namespace LinkEts2Nav.Core.Guidance;

/// <summary>Keeps route coordinates stable when the SDK trims the driven prefix or adds a tail.</summary>
public sealed class RouteProgressTracker
{
    private readonly record struct EdgeKey(string Id,ulong Start,ulong End);
    private Dictionary<EdgeKey,(double Start,int Count)> previous=[];
    private string? session;
    private long frame;
    public string Frame=>frame.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public void Reset(){previous.Clear();session=null;frame++;}
    public double Update(string sessionId,IReadOnlyList<DirectedRoad> route,double localAlong)
    {
        if(session!=sessionId){Reset();session=sessionId;}
        var sections=new List<(EdgeKey Key,double Start)>();double along=0;
        foreach(var e in route)
        {
            sections.Add((new(e.Id,e.StartNodeUid,e.EndNodeUid),along));
            along+=e.Points.Zip(e.Points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second));
        }
        var counts=sections.GroupBy(s=>s.Key).ToDictionary(g=>g.Key,g=>g.Count());
        var common=sections.Where(s=>counts[s.Key]==1&&previous.TryGetValue(s.Key,out var p)&&p.Count==1).ToArray();
        double offset=0;
        if(common.Length>0)offset=previous[common[0].Key].Start-common[0].Start;
        else if(previous.Count>0)frame++;
        previous=sections.GroupBy(s=>s.Key).ToDictionary(g=>g.Key,g=>(g.First().Start+offset,g.Count()));
        return localAlong+offset;
    }
}
