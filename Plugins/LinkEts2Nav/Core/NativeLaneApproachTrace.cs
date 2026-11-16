using LinkEts2Nav.Map;
namespace LinkEts2Nav.Core;

internal sealed record NativeLaneTraceStep(NativeLaneCurve? Curve,MapQueryIssue? Issue=null);

/// <summary>One native segment between topology origins; consumers may stop enumerating early.</summary>
internal static class NativeLaneApproachTrace
{
    internal static IEnumerable<NativeLaneTraceStep> Walk(NativeLaneCurve branch,int? target,
        IReadOnlyDictionary<int,NativeLaneCurve> curves,ILookup<int,int> incoming,ILookup<int,int> outgoing,
        bool forward,IReadOnlyList<int> interfaces,bool requireBoundary)
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
            // Upstream crosses a curve's end before its start; downstream is the mirror.
            var passed=(forward?current.Previous.Concat(incoming[current.Index]):current.Next.Concat(outgoing[current.Index])).Distinct().ToArray();
            if(passed.Length>1)
            {if(CheckOrigin(current,passed,!forward) is {} issue)yield return new(null,issue);yield break;}
            yield return new(current);
            if(interfaces.Contains(current.Index))yield break;
            var next=(forward?current.Next.Concat(outgoing[current.Index]):current.Previous.Concat(incoming[current.Index])).Distinct().ToArray();
            if(next.Length>1)
            {if(CheckOrigin(current,next,forward) is {} issue)yield return new(null,issue);yield break;}
            if(next.Length==0)
            {
                if(requireBoundary)yield return new(null,Invalid($"Native curve {current.Index}: chain ends without a confirmed prefab {(forward?"output":"input")} boundary."));
                yield break;
            }
            if(!curves.TryGetValue(next[0],out var continuation)
                ||!(forward?current.Next:current.Previous).Contains(next[0])||!(forward?continuation.Previous:continuation.Next).Contains(current.Index))
            {yield return new(null,Invalid($"Native curve {current.Index}: {(forward?"successor":"predecessor")} {next[0]} is unavailable or not reciprocal."));yield break;}
            current=continuation;
        }
        if(requireBoundary)yield return new(null,Invalid($"Native approach returned to its target origin at curve {target}."));
    }
}
