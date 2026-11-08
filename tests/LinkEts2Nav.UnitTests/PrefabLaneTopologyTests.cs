using LinkEts2Nav.Map;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;
public sealed class PrefabLaneTopologyTests
{
    [Fact]
    public void KeepsSeparateEntranceSlotsAndSharedDestinationWithoutGeometryInference()
    {
        var prefab=new Prefab { Nodes=[new Node{Uid=10},new Node{Uid=20},new Node{Uid=30}] };
        var descriptor=new PrefabDescriptor
        {
            Nodes=[new ControlNode{InputLines=[0,1],OutputLines=[]},
                new ControlNode{InputLines=[2],OutputLines=[]},
                new ControlNode{InputLines=[],OutputLines=[3,4]}],
            NavCurves=[new NavCurve{NextLines={3}},new NavCurve{NextLines={4}},
                new NavCurve{NextLines={4}},new NavCurve{NextLines={}},new NavCurve{NextLines={}}]
        };
        var lanes=PrefabLaneTopology.Build(prefab,descriptor)!;
        Assert.Equal(3,lanes.Count);
        Assert.Contains(lanes,c=>c.EntryNode==10 && c.EntryLane==1 && c.ExitNode==30 && c.ExitLane==1);
        Assert.Contains(lanes,c=>c.EntryNode==20 && c.EntryLane==0 && c.ExitNode==30 && c.ExitLane==1);
        prefab.ChangeOrigin(1);
        var rotated=PrefabLaneTopology.Build(prefab,descriptor)!;
        Assert.Equal(lanes.OrderBy(c=>c.EntryNode).ThenBy(c=>c.EntryLane),rotated.OrderBy(c=>c.EntryNode).ThenBy(c=>c.EntryLane));
        descriptor.NavCurves[0].NextLines.Clear(); descriptor.NavCurves[0].NextLines.Add(999);
        Assert.Null(PrefabLaneTopology.Build(prefab,descriptor));
    }
}
