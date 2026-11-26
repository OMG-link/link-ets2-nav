using System.Numerics;
using TruckLib.ScsMap;
using GameMap = TruckLib.ScsMap.Map;

namespace LinkEts2Nav.UnitTests;

internal static class FerryMapFixture
{
    public static Ferry AddPort(GameMap map, INode roadEndpoint, Vector3 activationPosition, string portId)
    {
        bool attachForward = (roadEndpoint.BackwardItem, roadEndpoint.ForwardItem) switch
        {
            (Road, null) => true,
            (null, Road) => false,
            _ => throw new InvalidOperationException("A ferry entrance needs a road endpoint with one free side.")
        };
        var ferry = Ferry.Add(map, activationPosition, portId);
        var prefab = new Prefab { Nodes = [roadEndpoint], Ferry = ferry };
        if (attachForward)
            roadEndpoint.ForwardItem = prefab;
        else
            roadEndpoint.BackwardItem = prefab;
        map.MapItems.Add(prefab.Uid, prefab);
        ferry.Prefab = prefab;
        return ferry;
    }
}
