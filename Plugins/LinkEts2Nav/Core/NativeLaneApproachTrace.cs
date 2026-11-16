using LinkEts2Nav.Map;
namespace LinkEts2Nav.Core;

internal sealed record NativeLaneTraceStep(NativeLaneCurve? Curve,MapQueryIssue? Issue=null);

/// <summary>One native segment between topology origins; consumers may stop enumerating early.</summary>
internal static class NativeLaneApproachTrace
{
    internal static IEnumerable<NativeLaneTraceStep> Upstream(NativeLaneCurve branch,int target,
        IReadOnlyDictionary<int,NativeLaneCurve> curves,ILookup<int,int> incoming,ILookup<int,int> outgoing)
    {
        MapQueryIssue Invalid(string reason)=>new MapQueryFailure(MapQueryFailureKind.InvalidData,reason);
        MapQueryIssue? CheckOrigin(NativeLaneCurve curve,int[] links,bool forward)
        {
            foreach(int index in links)
                if(!curves.TryGetValue(index,out var other)
                    ||!(forward?curve.Next:curve.Previous).Contains(index)
                    ||!(forward?other.Previous:other.Next).Contains(curve.Index))
                    return Invalid($"Native curve {curve.Index}: intervening {(forward?"split":"merge")} link {index} is unavailable or not reciprocal.");
            return null;
        }
        var seen=new HashSet<int>();var current=branch;
        while(current.Index!=target)
        {
            if(!seen.Add(current.Index))
            {yield return new(null,Invalid($"Native merge predecessor chain cycles at curve {current.Index}."));yield break;}
            var next=current.Next.Concat(outgoing[current.Index]).Distinct().ToArray();
            if(next.Length>1)
            {if(CheckOrigin(current,next,true) is {} issue)yield return new(null,issue);yield break;}
            yield return new(current);
            var previous=current.Previous.Concat(incoming[current.Index]).Distinct().ToArray();
            if(previous.Length!=1)
            {if(CheckOrigin(current,previous,false) is {} issue)yield return new(null,issue);yield break;}
            if(!curves.TryGetValue(previous[0],out var predecessor)
                ||!current.Previous.Contains(previous[0])||!predecessor.Next.Contains(current.Index))
            {yield return new(null,Invalid($"Native curve {current.Index}: predecessor {previous[0]} is unavailable or not reciprocal."));yield break;}
            current=predecessor;
        }
    }
}
