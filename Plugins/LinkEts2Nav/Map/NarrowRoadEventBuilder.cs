using System.Globalization;
using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
namespace LinkEts2Nav.Map;

/// <summary>Route occurrences of contiguous, explicitly single-track ordinary roads.</summary>
public static class NarrowRoadEventBuilder
{
    public const string Kind="narrow-road";
    public static IReadOnlyList<EnvironmentPreviewEvent> Build(IReadOnlyList<DirectedRoad> route,SpeedLimitData data,double originAlong=0)
    {
        if(data.Error is not null)return [];
        var result=new List<EnvironmentPreviewEvent>();double along=originAlong,start=0;
        DirectedRoad? first=null;ulong? previousEnd=null;
        void Finish()
        {
            if(first is null)return;
            // A trimmed route can begin in the middle of this same section.
            // Recover its known entry along the unique single-track chain so
            // trimming does not invent a second warning for the remaining half.
            var entry=first;double entryAlong=start;
            var seen=new HashSet<string>(StringComparer.Ordinal){entry.Id};
            while(true)
            {
                var previous=data.Roads.Values.Where(r=>r.SingleTrack&&!seen.Contains(r.Geometry.Id)
                    &&(r.Geometry.StartNodeUid==entry.StartNodeUid||r.Geometry.EndNodeUid==entry.StartNodeUid)).ToArray();
                if(previous.Length!=1)break;
                var geometry=previous[0].Geometry;
                if(geometry.StartNodeUid==entry.StartNodeUid)
                    geometry=geometry with{StartNodeUid=geometry.EndNodeUid,EndNodeUid=geometry.StartNodeUid,Points=geometry.Points.Reverse().ToArray()};
                entryAlong-=geometry.Points.Zip(geometry.Points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second));
                entry=geometry;seen.Add(entry.Id);
            }
            string id=string.Create(CultureInfo.InvariantCulture,$"narrow-road:{entry.Id}:{entry.StartNodeUid:X16}:{entry.EndNodeUid:X16}:{entryAlong:F1}");
            result.Add(new(id,Kind,entryAlong,along,Text));first=null;
        }
        foreach(var edge in route)
        {
            bool narrow=ulong.TryParse(edge.Id,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var uid)
                &&data.Roads.TryGetValue(uid,out var road)&&road.SingleTrack
                &&(road.Geometry.StartNodeUid==edge.StartNodeUid&&road.Geometry.EndNodeUid==edge.EndNodeUid
                    ||road.Geometry.StartNodeUid==edge.EndNodeUid&&road.Geometry.EndNodeUid==edge.StartNodeUid);
            if(!narrow||previousEnd!=edge.StartNodeUid)Finish();
            if(narrow&&first is null){first=edge;start=along;}
            along+=edge.Points.Zip(edge.Points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second));
            previousEnd=edge.EndNodeUid;
        }
        Finish();return result;
    }
    public static string Text(int meters)=>meters<50?"即将进入窄路，请减速观察。":
        NavigationPrompts.AtDistance(ManeuverKind.Straight,meters,0,"进入窄路，请减速观察");
}
