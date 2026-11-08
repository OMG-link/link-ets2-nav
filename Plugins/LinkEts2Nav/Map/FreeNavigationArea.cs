using System.Numerics;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
namespace LinkEts2Nav.Map;

/// <summary>PPD outline of a yard with spawn points but no lane navigation curves.</summary>
public sealed record FreeNavigationArea(string Id,IReadOnlyList<Vector3> Boundary,IReadOnlyList<ulong> ExitNodes)
{
    public bool Contains(Vector3 p)
    {
        if(Boundary.Count<3 || Math.Abs(p.Y-Boundary.Average(v=>v.Y))>3)return false;
        bool inside=false;
        for(int i=0,j=Boundary.Count-1;i<Boundary.Count;j=i++)
        {
            var a=Boundary[i];var b=Boundary[j];
            if((a.Z>p.Z)!=(b.Z>p.Z) && p.X<(b.X-a.X)*(p.Z-a.Z)/(b.Z-a.Z)+a.X)inside=!inside;
        }
        return inside;
    }
    public static IReadOnlyList<FreeNavigationArea> Build(Prefab prefab,PrefabDescriptor descriptor)
    {
        if(descriptor.NavCurves.Count!=0 || descriptor.SpawnPoints.Count==0
            || !PrefabPlacement.TryCreate(prefab,descriptor,out var placement))return [];
        var points=descriptor.MapPoints;
        var remaining=Enumerable.Range(0,points.Count)
            .Where(i=>points[i].Outline && (int)points[i].RoadSize==13).ToHashSet();
        var result=new List<FreeNavigationArea>();
        while(remaining.Count>0)
        {
            int start=remaining.Min(),previous=-1,current=start;
            var cycle=new List<int>();var visited=new HashSet<int>();bool valid=true;
            do
            {
                if(!visited.Add(current)){valid=false;break;}
                cycle.Add(current);remaining.Remove(current);
                var neighbors=points[current].Neighbors.Where(i=>i>=0 && i<points.Count
                    && points[i].Outline && (int)points[i].RoadSize==13).Distinct().ToArray();
                if(neighbors.Length!=2){valid=false;break;}
                int next=neighbors.First(i=>i!=previous);previous=current;current=next;
            }while(current!=start && cycle.Count<=points.Count);
            if(valid && current==start && cycle.Count>=3)
                result.Add(new(prefab.Uid.ToString("X16"),cycle.Select(i=>placement.ToWorld(points[i].Position)).ToArray(),
                    prefab.Nodes.Select(n=>n.Uid).ToArray()));
        }
        return result;
    }
}
