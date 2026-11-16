using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;

internal static class NativeLaneFixture
{
    // These synthetic fixtures explicitly declare their open ends to be prefab
    // ports. Production obtains these boundaries from PPD control-node slots.
    internal static NativeLaneGeometry WithInterfaces(ulong uid,IReadOnlyList<NativeLaneCurve> curves)=>new(uid,curves)
    {
        InputCurveIndices=curves.Where(c=>c.Previous.Count==0).Select(c=>c.Index).ToArray(),
        OutputCurveIndices=curves.Where(c=>c.Next.Count==0).Select(c=>c.Index).ToArray()
    };
}
