using TruckLib.Models.Ppd;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.Map;

/// <summary>Native PPD crossing groups distinguish at-grade junctions from forks and merges.</summary>
public static class PrefabJunctionClassifier
{
    // SCS Blender Tools PrefabLocators.PIF protocol constants:
    // https://github.com/SCSSoftware/BlenderTools/blob/master/addon/io_scs_tools/consts.py
    private const uint StartOrEnd = 0x00030000;
    private const uint SiblingMask = 0x000000F0;
    public static PrefabJunctionInfo Analyze(PrefabDescriptor descriptor)
    {
        int count=descriptor.NavCurves.Count;
        bool Valid(int i)=>i>=0&&i<count;
        int entrances=descriptor.Nodes.Count(n=>n.InputLines.Any(Valid));
        int exits=descriptor.Nodes.Count(n=>n.OutputLines.Any(Valid));
        if(entrances<2||exits<2||count>8192)return new(entrances,exits,0);
        var starts=Enumerable.Range(0,count).Select(_=>new HashSet<int>()).ToArray();
        var ends=Enumerable.Range(0,count).Select(_=>new HashSet<int>()).ToArray();
        var reverse=Enumerable.Range(0,count).Select(_=>new List<int>()).ToArray();
        for(int i=0;i<count;i++)foreach(int next in descriptor.NavCurves[i].NextLines.Where(Valid))reverse[next].Add(i);
        void Reach(int owner,IEnumerable<int> seeds,HashSet<int>[] owners,bool forward)
        {
            var pending=new Stack<int>(seeds.Where(Valid));
            while(pending.TryPop(out int i))
                if(owners[i].Add(owner))
                    foreach(int next in forward?descriptor.NavCurves[i].NextLines.Where(Valid):reverse[i])pending.Push(next);
        }
        for(int n=0;n<descriptor.Nodes.Count;n++)
        {
            Reach(n,descriptor.Nodes[n].InputLines,starts,true);
            Reach(n,descriptor.Nodes[n].OutputLines,ends,false);
        }
        int crossings=0;
        for(int i=0;i<descriptor.Intersections.Count;i++)
        {
            var head=descriptor.Intersections[i];uint flags=head.Flags.Bits;
            int siblings=(int)((flags&SiblingMask)>>4);
            if(siblings<2||i+siblings>descriptor.Intersections.Count)continue;
            if((flags&StartOrEnd)==0)
            {
                var curves=descriptor.Intersections.Skip(i).Take(siblings).Select(p=>(int)p.CurveId).ToArray();
                if(curves.All(Valid)&&curves.Distinct().Count()==curves.Length)
                {
                    bool crossed=false;
                    for(int a=0;a<curves.Length&&!crossed;a++)for(int b=a+1;b<curves.Length;b++)
                        if(starts[curves[a]].Any(x=>starts[curves[b]].Any(y=>x!=y))
                            &&ends[curves[a]].Any(x=>ends[curves[b]].Any(y=>x!=y))) {crossed=true;break;}
                    if(crossed)crossings++;
                }
            }
            i+=siblings-1;
        }
        return new(entrances,exits,crossings);
    }
}
