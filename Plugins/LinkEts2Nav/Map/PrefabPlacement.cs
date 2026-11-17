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
    public Vector3 ToWorldDirection(Vector3 direction)
        => new(direction.X*cos-direction.Z*sin,direction.Y,direction.X*sin+direction.Z*cos);
    public static bool TryCreate(Prefab prefab,PrefabDescriptor descriptor,out PrefabPlacement placement)
    {
        ValidateTopology(prefab,descriptor);
        placement=null!;int count=descriptor.Nodes.Count;
        var control=Enumerable.Range(0,count).Select(i=>descriptor.Nodes[(i+prefab.Origin)%count]).ToArray();
        if(count==1)
        {
            var node=prefab.Nodes[0];var direction=control[0].Direction;var q=node.Rotation;
            if(!Finite(node.Position)||!Finite(direction)||Horizontal(direction)<.001f
                ||!float.IsFinite(q.X)||!float.IsFinite(q.Y)||!float.IsFinite(q.Z)||!float.IsFinite(q.W)||q.LengthSquared()<.000001f)
                throw new InvalidDataException($"Prefab {prefab.Uid:X16}: invalid control-node placement.");
            // The map node's rotation is relative to the model control direction,
            // using the same reorientation as TruckLib's prefab placement.
            var relative=Quaternion.Normalize(q*Quaternion.Conjugate(TruckLib.MathEx.GetNodeRotation(direction)));
            var axis=Vector3.Transform(Vector3.UnitX,relative);
            if(Horizontal(axis)<.001f)throw new InvalidDataException($"Prefab {prefab.Uid:X16}: control-node rotation has no horizontal axis.");
            placement=new(control,node.Position,MathF.Atan2(axis.Z,axis.X));return true;
        }
        int other=Enumerable.Range(1,count-1).MaxBy(i=>Horizontal(control[i].Position-control[0].Position));
        var localDelta=control[other].Position-control[0].Position;
        var worldDelta=prefab.Nodes[other].Position-prefab.Nodes[0].Position;
        float localLength=Horizontal(localDelta),worldLength=Horizontal(worldDelta);
        if(!float.IsFinite(localLength)||!float.IsFinite(worldLength)||localLength==0||worldLength==0)
            throw new InvalidDataException($"Prefab {prefab.Uid:X16}: control nodes do not define a horizontal placement.");
        float yaw=MathF.Atan2(worldDelta.Z,worldDelta.X)-MathF.Atan2(localDelta.Z,localDelta.X);
        var candidate=new PrefabPlacement(control,prefab.Nodes[0].Position,yaw);
        for(int i=0;i<count;i++)
            if(!Finite(prefab.Nodes[i].Position))throw new InvalidDataException($"Prefab {prefab.Uid:X16}: nonfinite control-node position.");
        placement=candidate;return true;
    }
    internal static void ValidateTopology(Prefab prefab,PrefabDescriptor descriptor)
    {
        if(descriptor.Nodes.Count<1||prefab.Nodes.Count!=descriptor.Nodes.Count||prefab.Origin>=descriptor.Nodes.Count)
            throw new InvalidDataException($"Prefab {prefab.Uid:X16}: instance has {prefab.Nodes.Count} nodes and origin {prefab.Origin}, descriptor has {descriptor.Nodes.Count} nodes.");
    }
    private static float Horizontal(Vector3 p)=>MathF.Sqrt(p.X*p.X+p.Z*p.Z);
    private static bool Finite(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
}
