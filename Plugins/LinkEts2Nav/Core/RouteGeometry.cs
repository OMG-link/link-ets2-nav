using System.Numerics;

namespace LinkEts2Nav.Core;

/// <summary>Route mileage counts actual edge segments, never artificial links across a shared interface.</summary>
public static class RouteGeometry
{
    public static double Length(DirectedRoad edge)
    {
        double length = 0;
        for (int i = 1; i < edge.Points.Count; i++)
            if (!edge.SegmentStarts.Contains(i)) length += Vector3.Distance(edge.Points[i - 1], edge.Points[i]);
        return length;
    }
}
