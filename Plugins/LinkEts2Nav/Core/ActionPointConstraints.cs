using LinkEts2Nav.Core.Guidance;

namespace LinkEts2Nav.Core;

/// <summary>Final cue anchors respect the preceding physical series' exit; no progress or ordering state is retained.</summary>
public static class ActionPointConstraints
{
    public static IReadOnlyList<NavigationManeuver> Apply(IReadOnlyList<NavigationManeuver> actions)
    {
        var adjusted=new Dictionary<NavigationManeuver,NavigationManeuver>();
        // Ends are route coordinates supplied by the physical structures, independent
        // of an auxiliary lane's earlier candidate anchor. An explicit series keeps
        // all its children together; repeated visits have different exit coordinates.
        var visits=actions.Where(NavigationEventPlanner.IsNavigationAction)
            .GroupBy(a=>(Series:a.SpeechSequenceId??a.Id,End:a.EndDistanceMeters))
            .OrderBy(g=>g.Key.End??g.Min(a=>a.DistanceMeters));
        double? previousEnd=null;
        foreach(var visit in visits)
        {
            foreach(var action in visit)
                adjusted[action]=previousEnd is {} boundary&&action.DistanceMeters<boundary
                    ?action with{DistanceMeters=boundary}:action;
            previousEnd=visit.Key.End;
        }
        return actions.Select(a=>adjusted.GetValueOrDefault(a,a)).OrderBy(a=>a.DistanceMeters).ToArray();
    }
}
