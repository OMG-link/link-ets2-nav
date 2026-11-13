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
/// Uses host-owned lightweight roads/nodes only. Per-call route spans are released
/// afterwards; no route-sector scan, PPD parsing or full-map geometry copy is performed.
/// Prefabs use interface chords, scaled uniformly by the optional measured factor.
/// </summary>
public sealed class RouteJourneyEstimator(GameMap host,Func<Road,bool,SpeedLimitResult> categorySpeed,double prefabDistanceFactor=1)
{
    private sealed record Span(MapItem Item,INode Start,INode End,double Length,bool Forward);
    private static RouteJourneyEstimate Missing(string reason)=>new(null,null,reason);

    public RouteJourneyEstimate Estimate(RouteSnapshot route,Vector3 position,Vector3 heading)
    {
        if(!double.IsFinite(prefabDistanceFactor)||prefabDistanceFactor<=0)return Missing("预制件距离系数无效");
        if(route.Availability!=RouteAvailability.Available||route.MayBeTruncated||route.Nodes.Count<2)
            return Missing("完整导航路线尚未就绪或已截断");
        var nodes=route.Nodes.Where((n,i)=>i==0||n!=route.Nodes[i-1]).ToArray();
        if(nodes.Length<2)return Missing("导航路线不足两个节点");
        if(nodes.Zip(nodes.Skip(1)).All(p=>p.First.DistanceToEndMeters<p.Second.DistanceToEndMeters))Array.Reverse(nodes);
        if(!nodes.Zip(nodes.Skip(1)).All(p=>p.First.DistanceToEndMeters>p.Second.DistanceToEndMeters)
            ||nodes[^1].DistanceToEndMeters>.01f)return Missing("导航终端或节点顺序未确认");
        var spans=new List<Span>();
        for(int i=0;i<nodes.Length-1;i++)
        {
            if(!TryNode(nodes[i].Uid,out var start))return Missing($"导航物理节点未加载：{nodes[i].Uid:X16}");
            var owners=Owners(start).ToArray();Span? span=null;int stop=i+1;
            if(TryNode(nodes[stop].Uid,out var direct))
            {
                var roads=owners.OfType<Road>().Where(r=>Joins(r,start.Uid,direct.Uid)).ToArray();
                if(roads.Length>1)return Missing("导航道路连接不唯一");
                if(roads.Length==1)
                {
                    if(owners.OfType<Prefab>().Any(p=>p.Nodes.Any(n=>n.Uid==start.Uid)&&p.Nodes.Any(n=>n.Uid==direct.Uid)))
                        return Missing("导航道路和预制件连接归属不唯一");
                    var road=roads[0];span=new(road,start,direct,road.Length,road.Node.Uid==start.Uid);
                }
            }
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
                            if(end.Uid!=start.Uid)candidates.Add((new(prefab,start,end,
                                Vector3.Distance(start.Position,end.Position)*prefabDistanceFactor,true),j));
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
        if(!Finite(position)||!Finite(heading))return Missing("玩家位置或朝向无效");
        var matches=new List<(int Index,double Fraction,double Error)>();
        for(int i=0;i<spans.Count;i++)
        {
            var span=spans[i];
            if(Vector3.Distance(position,span.Start.Position)>800&&Vector3.Distance(position,span.End.Position)>800)continue;
            if(matches.Any(m=>spans[m.Index].Item.Uid==span.Item.Uid
                &&spans[m.Index].Start.Uid==span.Start.Uid&&spans[m.Index].End.Uid==span.End.Uid))continue;
            var projection=Project(span,position,heading);
            if(projection is {} p)matches.Add((i,p.Fraction,p.Error));
        }
        var ordered=matches.OrderBy(p=>p.Error).ToArray();
        if(ordered.Length==0)return Missing("当前位置未匹配导航路线道路或预制件接口弦");
        if(ordered.Length>1&&Math.Abs(ordered[0].Error-ordered[1].Error)<.01
            &&spans[ordered[0].Index].End.Uid!=spans[ordered[1].Index].Start.Uid
            &&spans[ordered[0].Index].Start.Uid!=spans[ordered[1].Index].End.Uid)
            return Missing("当前位置在导航路线上的归属不唯一");
        var current=ordered[0];
        var speeds=new double?[spans.Count];
        for(int i=0;i<spans.Count;i++)if(spans[i].Item is Road road)
        {
            var speed=categorySpeed(road,spans[i].Forward);
            if(speed.Kph is not >0&&i>=current.Index)return Missing($"道路类别限速未就绪：{road.Uid:X16}；{speed.Reason}");
            speeds[i]=speed.Kph is >0?speed.Kph:null;
        }
        // Consecutive prefabs borrow the closest road category on either side.
        // Where both exist, the slower category provides a conservative estimate.
        double? previous=null;var behind=new double?[spans.Count];
        for(int i=0;i<spans.Count;i++){if(speeds[i] is {} speed)previous=speed;behind[i]=previous;}
        double? following=null;
        for(int i=spans.Count-1;i>=0;i--)
        {
            if(spans[i].Item is Road){following=speeds[i];continue;}
            speeds[i]=behind[i] is {} a&&following is {} b?Math.Min(a,b):behind[i]??following;
            if(speeds[i] is null&&i>=current.Index)return Missing("预制件没有可用的相邻道路类别限速");
        }
        double meters=0,seconds=0;
        for(int i=current.Index;i<spans.Count;i++)
        {
            double length=spans[i].Length*(i==current.Index?1-current.Fraction:1);
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
    private static bool Finite(Vector3 point)=>float.IsFinite(point.X)&&float.IsFinite(point.Y)&&float.IsFinite(point.Z);
    private static (double Fraction,double Error)? Project(Span span,Vector3 position,Vector3 heading)
    {
        var points=span.Item is Road road?Enumerable.Range(0,65).Select(i=>road.InterpolateCurve(span.Forward?i/64f:1-i/64f).Position).ToArray()
            :new[]{span.Start.Position,span.End.Position};
        double total=points.Zip(points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second));
        if(total<=0)return null;
        double offset=0,best=double.PositiveInfinity,along=0;
        for(int i=1;i<points.Length;i++)
        {
            var delta=points[i]-points[i-1];double length=delta.Length();
            if(length<=0)continue;
            var direction=new Vector2(delta.X,delta.Z);var forward=new Vector2(heading.X,heading.Z);
            if(direction.LengthSquared()<=0||forward.LengthSquared()<=0){offset+=length;continue;}
            double t=Math.Clamp(Vector3.Dot(position-points[i-1],delta)/delta.LengthSquared(),0,1);
            var nearest=points[i-1]+delta*(float)t;double error=Vector3.DistanceSquared(position,nearest);
            if(Vector2.Dot(Vector2.Normalize(direction),Vector2.Normalize(forward))>=.25f
                &&Math.Abs(position.Y-nearest.Y)<=6&&error<=400&&error<best)
            {best=error;along=offset+length*t;}
            offset+=length;
        }
        return double.IsFinite(best)?(along/total,best):null;
    }
}
