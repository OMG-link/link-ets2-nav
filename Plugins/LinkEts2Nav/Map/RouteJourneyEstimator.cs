using System.Numerics;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
using GameMap=TruckLib.ScsMap.Map;

namespace LinkEts2Nav.Map;

public sealed record RouteJourneyEstimate(double? WorldMeters,double? RealSeconds,string? Failure)
{
    public bool Ready=>WorldMeters is not null&&RealSeconds is not null&&Failure is null;
}

/// <summary>
/// Uses host-owned lightweight roads/nodes only. Estimates the supplied route from its first interface, independently of vehicle pose.
/// Per-call route spans are released
/// afterwards; no route-sector scan, PPD parsing or full-map geometry copy is performed.
/// Prefabs use interface chords, scaled uniformly by the optional measured factor.
/// </summary>
public sealed class RouteJourneyEstimator(GameMap host,Func<Road,bool,SpeedLimitResult> categorySpeed,double prefabDistanceFactor=1,FerryJourneyIndex? ferries=null)
{
    private sealed record Span(MapItem? Item,double Length,bool Forward,bool IsTransfer=false);
    private static RouteJourneyEstimate Missing(string reason)=>new(null,null,reason);

    public RouteJourneyEstimate Estimate(RouteSnapshot route)
    {
        if(!double.IsFinite(prefabDistanceFactor)||prefabDistanceFactor<=0)return Missing("预制件距离系数无效");
        if(route.Availability!=RouteAvailability.Available||route.MayBeTruncated||route.Nodes.Count<2)
            return Missing("完整导航路线尚未就绪或已截断");
        var nodes=route.Nodes.Where((n,i)=>i==0||n!=route.Nodes[i-1]).ToArray();
        if(nodes.Length<2)return Missing("导航路线不足两个节点");
        if(nodes[0].DistanceToEndMeters<nodes[^1].DistanceToEndMeters)Array.Reverse(nodes);
        if(!nodes.Zip(nodes.Skip(1)).All(p=>p.First.DistanceToEndMeters>=p.Second.DistanceToEndMeters)
            ||nodes[^1].DistanceToEndMeters>.01f)return Missing("导航终端或节点顺序未确认");
        var spans=new List<Span>();
        for(int i=0;i<nodes.Length-1;i++)
        {
            bool transfer=ferries?.Connects(nodes[i].Uid,nodes[i+1].Uid)==true;
            if(!TryNode(nodes[i].Uid,out var start))
            {
                if(transfer){spans.Add(new(null,0,true,true));continue;}
                return Missing($"导航物理节点未加载：{nodes[i].Uid:X16}");
            }
            var owners=Owners(start).ToArray();Span? span=null;int stop=i+1;
            if(TryNode(nodes[stop].Uid,out var direct))
            {
                var roads=owners.OfType<Road>().Where(r=>Joins(r,start.Uid,direct.Uid)).ToArray();
                if(roads.Length>1)return Missing("导航道路连接不唯一");
                if(roads.Length==1)
                {
                    if(owners.OfType<Prefab>().Any(p=>p.Nodes.Any(n=>n.Uid==start.Uid)&&p.Nodes.Any(n=>n.Uid==direct.Uid)))
                        return Missing("导航道路和预制件连接归属不唯一");
                    var road=roads[0];span=new(road,road.Length,road.Node.Uid==start.Uid);
                }
            }
            if(span is null&&transfer)span=new(null,0,true,true);
            if(span is null)
            {
                // Unknown internal UID records may be crossed only between two
                // actual interfaces of one common prefab. A known different road
                // boundary ends the search; it is never silently bridged.
                var candidates=new List<(Span Span,int Stop)>();
                foreach(var prefab in owners.OfType<Prefab>().Where(p=>p.Nodes.Any(n=>n.Uid==start.Uid)))
                    for(int j=i+1;j<nodes.Length;j++)
                    {
                        if(!TryNode(nodes[j].Uid,out var end))continue;
                        if(prefab.Nodes.Any(n=>n.Uid==end.Uid))
                        {
                            if(end.Uid!=start.Uid)candidates.Add((new(prefab,Vector3.Distance(start.Position,end.Position)*prefabDistanceFactor,true),j));
                            break;
                        }
                        if(Owners(end).Any(o=>o.Uid!=prefab.Uid))break;
                    }
                if(candidates.Count!=1)return Missing($"导航连接缺失或归属不唯一：{start.Uid:X16}");
                (span,stop)=candidates[0];
            }
            if(!double.IsFinite(span.Length)||span.Length<0||span.Item is Road&&span.Length==0)return Missing("宿主道路长度无效");
            spans.Add(span);i=stop-1;
        }
        var speeds=new double?[spans.Count];
        for(int i=0;i<spans.Count;i++)if(spans[i].Item is Road road)
        {
            var speed=categorySpeed(road,spans[i].Forward);
            if(speed.Kph is not >0)return Missing($"道路类别限速未就绪：{road.Uid:X16}；{speed.Reason}");
            speeds[i]=speed.Kph is >0?speed.Kph:null;
        }
        // Consecutive prefabs borrow the closest road category on either side.
        // Where both exist, the slower category provides a conservative estimate.
        double? previous=null;var behind=new double?[spans.Count];
        for(int i=0;i<spans.Count;i++){if(spans[i].IsTransfer){previous=null;continue;}if(speeds[i] is {} speed)previous=speed;behind[i]=previous;}
        double? following=null;
        for(int i=spans.Count-1;i>=0;i--)
        {
            if(spans[i].IsTransfer){following=null;continue;}
            if(spans[i].Item is Road){following=speeds[i];continue;}
            speeds[i]=behind[i] is {} a&&following is {} b?Math.Min(a,b):behind[i]??following;
            if(speeds[i] is null)return Missing("预制件没有可用的相邻道路类别限速");
        }
        double meters=0,seconds=0;
        for(int i=0;i<spans.Count;i++)
        {
            if(spans[i].IsTransfer){seconds+=60;continue;}
            double length=spans[i].Length;
            meters+=length;seconds+=length/(speeds[i]!.Value*.8/3.6);
        }
        return new(meters,seconds,null);
    }

    private bool TryNode(ulong uid,out INode node)
    {
        node=null!;
        // TruckLib's internal unresolved-node implementation throws on access.
        if(!host.Nodes.TryGetValue(uid,out var candidate)||candidate is not Node)return false;
        node=candidate;return true;
    }
    private IEnumerable<MapItem> Owners(INode node)
        =>new[]{node.ForwardItem,node.BackwardItem}.Where(o=>o is not null).DistinctBy(o=>o.Uid)
            .Select(o=>host.MapItems.TryGetValue(o.Uid,out var actual)?actual:o as MapItem).OfType<MapItem>()
            .Where(o=>o is Road or Prefab);
    private static bool Joins(Road road,ulong a,ulong b)
        =>road.Node.Uid==a&&road.ForwardNode.Uid==b||road.Node.Uid==b&&road.ForwardNode.Uid==a;
}
