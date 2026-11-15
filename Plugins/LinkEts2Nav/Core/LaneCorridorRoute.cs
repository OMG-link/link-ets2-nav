namespace LinkEts2Nav.Core;

/// <summary>Queries existing native corridor facts in a route frame; never reads resources or edits map data.</summary>
internal static class LaneCorridorRoute
{
    public static LaneActionInterval? Interval(IReadOnlyList<DirectedRoad> route,double[] offsets,AuxiliaryLaneCorridor corridor,AuxiliaryLaneTerminal terminal)
    {
        int end=Find(route,terminal.Edge);if(end<0)return null;
        foreach(var section in corridor.Sections)
        {
            int at=Find(route,section.Edge);if(at<0)continue;
            return new(offsets[at]+section.StartMeters-section.CorridorAlong,offsets[end]+terminal.EndMeters);
        }
        return null;
    }
    public static int Find(IReadOnlyList<DirectedRoad> route,DirectedRoad edge)
    {var key=NativeRoadKey.Of(edge);for(int i=0;i<route.Count;i++)if(NativeRoadKey.Of(route[i])==key)return i;return -1;}
}
