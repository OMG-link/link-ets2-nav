using System.Numerics;
using LinkEts2Nav.Core;

namespace LinkEts2Nav.Map;

/// <summary>Observed lane choices on SDK visits where different native paths lead around the same ring or return.</summary>
internal sealed class ObservedRouteLanes
{
    internal sealed record Choice(int Visit,PrefabLanePath Lane,IReadOnlyList<DirectedRoad> Path);
    private sealed record Passage(int Visit,DirectedRoad Edge,Vector3 Minimum,Vector3 Maximum);
    private readonly IReadOnlyList<DirectedRoad> baseline;
    private readonly DirectedRoad[] original;
    private readonly Passage[] visits;
    private IReadOnlyList<DirectedRoad> precedingChoices;
    private int currentVisit=-1;
    private readonly Dictionary<PrefabLanePath,IReadOnlyList<DirectedRoad>> choices=new(ReferenceEqualityComparer.Instance);
    public IReadOnlyList<DirectedRoad> Current {get;private set;}

    private ObservedRouteLanes(IReadOnlyList<DirectedRoad> route,Passage[] visits)
    {
        this.visits=visits;
        original=route.ToArray();
        baseline=Current=precedingChoices=route;
    }

    public static ObservedRouteLanes? Create(IReadOnlyList<DirectedRoad> route,IReadOnlyDictionary<ulong,RoundaboutStructure>? rings)
    {
        var visits=new List<Passage>();
        for(int i=0;i<route.Count;i++)
        {
            var edge=route[i];
            if(!edge.ControlPositions.HasValue || edge.LanePaths.Count<2
                || !edge.ReturnsToEntry && rings?.ContainsKey(edge.LanePaths[0].PrefabUid)!=true
                || edge.LanePaths.Max(l=>l.LengthMeters)-edge.LanePaths.Min(l=>l.LengthMeters)<=LaneRouteConnections.NativeSeamMeters)continue;
            var points=edge.LanePaths.SelectMany(l=>l.Points);
            visits.Add(new(i,edge,points.Aggregate(Vector3.Min),points.Aggregate(Vector3.Max)));
        }
        return visits.Count>0?new(route,visits.ToArray()):null;
    }

    public bool Matches(IReadOnlyList<DirectedRoad> path)
    {
        if(original.Length!=path.Count)return false;
        for(int i=0;i<original.Length;i++)if(!ReferenceEquals(original[i],path[i]))return false;
        return true;
    }
    public void Reset()
    {
        Current=precedingChoices=baseline;
        choices.Clear();currentVisit=-1;
    }

    public Choice? Propose(Vector3 position,Vector3 heading,int firstVisit,DirectedPathMatcher matcher,
        Func<DirectedRoad,bool> ownsPassage)
    {
        foreach(var passage in visits)
        {
            int visit=passage.Visit;
            if(visit<firstVisit || visit<currentVisit)continue;
            if(!matcher.Near(position,passage.Minimum,passage.Maximum))continue;
            var edge=passage.Edge;
            PrefabLanePath? best=null;double nearest=double.PositiveInfinity,second=double.PositiveInfinity;
            foreach(var lane in edge.LanePaths)
            {
                if(matcher.ProjectionError(position,heading,lane.Points) is not {} error)continue;
                if(error<nearest){second=nearest;nearest=error;best=lane;}
                else second=Math.Min(second,error);
            }
            if(best is null)continue;
            // Multiple route occurrences can use this same physical passage.
            // The earliest live one owns the observation until the SDK advances.
            if(!ownsPassage(edge))continue;
            if(second<=nearest+DirectedPathMatcher.ProjectionTieMeters || ReferenceEquals(Current[visit].ObservedLane,best))return null;
            if(visit==currentVisit && choices.TryGetValue(best,out var known))return new(visit,best,known);
            var copy=(visit==currentVisit?precedingChoices:Current).ToArray();
            copy[visit]=PrefabPathGeometry.WithObservedLane(edge,best);
            return new(visit,best,copy);
        }
        return null;
    }

    public void Accept(Choice choice)
    {
        if(currentVisit!=choice.Visit)
        {
            precedingChoices=Current;currentVisit=choice.Visit;
            choices.Clear();
        }
        choices.TryAdd(choice.Lane,choice.Path);
        Current=choice.Path;
        DirectedPathMatcher.RegisterComponent(Current);
    }
}
