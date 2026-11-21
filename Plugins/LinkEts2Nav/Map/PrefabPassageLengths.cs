using TruckLib.Models.Ppd;
using TruckLib.ScsMap;

namespace LinkEts2Nav.Map;

/// <summary>Native PPD chain lengths between map interfaces, including returns to the same interface.</summary>
public static class PrefabPassageLengths
{
    public static IReadOnlyList<double> Read(Prefab prefab,PrefabDescriptor descriptor,ulong entry,ulong exit)
    {
        PrefabDescriptorInput.AssertInstance(prefab,descriptor);
        PrefabDescriptorInput.AssertCurveIndices(descriptor);
        var from=PrefabLaneTopology.ControlOf(prefab,descriptor,entry);
        var to=PrefabLaneTopology.ControlOf(prefab,descriptor,exit);
        if(from is null||to is null)return [];
        var targets=to.OutputLines.Where(i=>i!=-1).ToHashSet();
        var lengths=new List<double>();
        foreach(int start in from.InputLines.Where(i=>i!=-1).Distinct())
            foreach(var chain in PrefabCurvePathfinder.FindEach(descriptor,start,targets,prefab.Uid).Values)
            {
                double length=chain.Sum(i=>(double)descriptor.NavCurves[i].Length);
                if(length>0)lengths.Add(length);
            }
        return Array.AsReadOnly(lengths.Distinct().Order().ToArray());
    }
}
