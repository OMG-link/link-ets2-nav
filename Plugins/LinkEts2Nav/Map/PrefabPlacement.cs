using System.Numerics;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
namespace LinkEts2Nav.Map;

/// <summary>Validated horizontal rigid placement shared by PPD paths and area metadata.</summary>
public sealed class PrefabPlacement
{
    public IReadOnlyList<ControlNode> ControlNodes { get; }
    private readonly Vector3 localOrigin,worldOrigin;
    private readonly float sin,cos;
    private PrefabPlacement(ControlNode[] controls,Vector3 world,float yaw)
    { ControlNodes=controls;localOrigin=controls[0].Position;worldOrigin=world;sin=MathF.Sin(yaw);cos=MathF.Cos(yaw); }
    public Vector3 ToWorld(Vector3 point)
    {
        var p=point-localOrigin;
        return worldOrigin+new Vector3(p.X*cos-p.Z*sin,p.Y,p.X*sin+p.Z*cos);
    }
    public static bool TryCreate(Prefab prefab,PrefabDescriptor descriptor,out PrefabPlacement placement)
    {
        placement=null!;int count=descriptor.Nodes.Count;
        if(count<2 || prefab.Nodes.Count!=count || prefab.Origin>=count)return false;
        var control=Enumerable.Range(0,count).Select(i=>descriptor.Nodes[(i+prefab.Origin)%count]).ToArray();
        int other=Enumerable.Range(1,count-1).MaxBy(i=>Horizontal(control[i].Position-control[0].Position));
        var localDelta=control[other].Position-control[0].Position;
        var worldDelta=prefab.Nodes[other].Position-prefab.Nodes[0].Position;
        float localLength=Horizontal(localDelta),worldLength=Horizontal(worldDelta);
        if(localLength<1 || Math.Abs(worldLength-localLength)>.5f)return false;
        float yaw=MathF.Atan2(worldDelta.Z,worldDelta.X)-MathF.Atan2(localDelta.Z,localDelta.X);
        var candidate=new PrefabPlacement(control,prefab.Nodes[0].Position,yaw);
        for(int i=0;i<count;i++)
            if(!Finite(prefab.Nodes[i].Position)||Horizontal(candidate.ToWorld(control[i].Position)-prefab.Nodes[i].Position)>.5f)return false;
        placement=candidate;return true;
    }
    private static float Horizontal(Vector3 p)=>MathF.Sqrt(p.X*p.X+p.Z*p.Z);
    private static bool Finite(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
}
