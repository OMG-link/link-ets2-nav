using TruckLib.Models.Ppd;
using TruckLib.ScsMap;

namespace LinkEts2Nav.Map;

/// <summary>Native PPD chain lengths between map interfaces, including returns to the same interface.</summary>
public static class PrefabPassageLengths
{
    public static IReadOnlyList<double> Read(Prefab prefab,PrefabDescriptor descriptor,ulong entry,ulong exit)
    {
        int nodes=descriptor.Nodes.Count;
        if(nodes==0||prefab.Nodes.Count!=nodes||prefab.Origin>=nodes)return [];
        int from=-1,to=-1;
        for(int i=0;i<prefab.Nodes.Count;i++)
        {if(prefab.Nodes[i].Uid==entry)from=i;if(prefab.Nodes[i].Uid==exit)to=i;}
        if(from<0||to<0)return [];
        int count=descriptor.NavCurves.Count;
        var targets=descriptor.Nodes[(to+prefab.Origin)%nodes].OutputLines.Where(i=>i>=0&&i<count).ToHashSet();
        var lengths=new List<double>();
        foreach(int start in descriptor.Nodes[(from+prefab.Origin)%nodes].InputLines.Where(i=>i>=0&&i<count).Distinct())
            foreach(var chain in PrefabCurvePathfinder.FindEach(descriptor,start,targets).Values)
            {
                double length=chain.Sum(i=>(double)descriptor.NavCurves[i].Length);
                if(double.IsFinite(length)&&length>0)lengths.Add(length);
            }
        return Array.AsReadOnly(lengths.Distinct().Order().ToArray());
    }
}
