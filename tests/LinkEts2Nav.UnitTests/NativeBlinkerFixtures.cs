using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;

// Native Hermite geometry captured from the named game PPDs; no synthetic joins.
internal static class NativeBlinkerFixtures
{
    // /prefab2/fork_temp/ger/ger_r1_exit_mirrored_oneway.ppd
    internal static NativeLaneGeometry GermanIndependentGroups()=>new(42,[
        new(0,new(2.7567854f,0f,-30.000029f),new(1.4321898f,5.0750355e-11f,-23.372137f),new(1.183448e-06f,0f,6.7685337f),new(-2.147687f,0f,6.4187617f),[8],[]){Blinker=LaneBlinker.NoBlinker},
        new(1,new(2.25f,0f,-2.25f),new(4.120579f,0f,-18.356918f),new(0f,0f,-16.223513f),new(4.12382f,0f,-15.690649f),[9],[6]){Blinker=LaneBlinker.NoBlinker},
        new(2,new(2.25f,0f,-2.25f),new(10.315858f,0f,-22.752157f),new(0f,0f,-22.066738f),new(15.603536f,0f,-15.603549f),[10],[6]){Blinker=LaneBlinker.NoBlinker},
        new(3,new(-2.1643188f,0f,-5.5967884f),new(-2.25f,0f,0f),new(-0.11925626f,0f,5.5962467f),new(9.78701e-07f,0f,5.5975175f),[],[4,5]){Blinker=LaneBlinker.NoBlinker},
        new(4,new(-1.75f,0f,-27.750029f),new(-2.1643188f,0f,-5.5967884f),new(3.874089e-06f,0f,22.157206f),new(-0.4720638f,0f,22.152176f),[3],[7]){Blinker=LaneBlinker.NoBlinker},
        new(5,new(-1.5785984f,0f,-13.892191f),new(-2.1643188f,0f,-5.5967884f),new(-1.398859f,0f,8.200726f),new(-0.17724177f,0f,8.317288f),[3],[8]){Blinker=LaneBlinker.NoBlinker},
        new(6,new(2.25f,0f,0f),new(2.25f,0f,-2.25f),new(0f,0f,-2.25f),new(0f,0f,-2.25f),[1,2],[]){Blinker=LaneBlinker.NoBlinker},
        new(7,new(-1.7432145f,0f,-30.000029f),new(-1.75f,0f,-27.750029f),new(3.934114e-07f,0f,2.2500508f),new(3.934114e-07f,0f,2.2500508f),[4],[]){Blinker=LaneBlinker.NoBlinker},
        new(8,new(1.4321898f,5.0750355e-11f,-23.372137f),new(-1.5785984f,0f,-13.892191f),new(-3.157781f,0f,9.437615f),new(-1.673398f,0f,9.810194f),[5],[0]){Blinker=LaneBlinker.Right},
        new(9,new(4.120579f,0f,-18.356918f),new(8.73172f,0f,-27.525127f),new(2.6135314f,0f,-9.944179f),new(7.2703915f,0f,-7.2703977f),[],[1]){Blinker=LaneBlinker.Left},
        new(10,new(10.315858f,0f,-22.752157f),new(11.913701f,0f,-24.343147f),new(1.594434f,0f,-1.5944355f),new(1.594434f,0f,-1.5944355f),[],[2]){Blinker=LaneBlinker.NoBlinker}]);
    // /prefab2/fork_temp/un/un_fork_4m-1m_0-2_0-3_join_0-2_60m.ppd
    internal static NativeLaneGeometry UniversalJoin()=>new(43,[
        new(0,new(24424.115f,7.9804688f,-48278.906f),new(24423.342f,7.9804688f,-48277.62f),new(-0.7726201f,0f,1.2857128f),new(-0.7726201f,0f,1.2857128f),[],[12]){Blinker=LaneBlinker.NoBlinker},
        new(1,new(24445.31f,7.9804688f,-48333.363f),new(24444.898f,7.9804688f,-48331.92f),new(-0.4135268f,0f,1.4418726f),new(-0.4135268f,0f,1.4418726f),[2],[]){Blinker=LaneBlinker.NoBlinker},
        new(2,new(24444.898f,7.9804688f,-48331.92f),new(24444.031f,7.9804688f,-48329.016f),new(-0.8358421f,0f,2.9143884f),new(-0.88151634f,0f,2.9009001f),[3],[1]){Blinker=LaneBlinker.NoBlinker},
        new(3,new(24444.031f,7.9804688f,-48329.016f),new(24420.258f,7.9804688f,-48281.227f),new(-15.521818f,0f,51.079308f),new(-27.49786f,0f,45.75904f),[4],[2]){Blinker=LaneBlinker.Right},
        new(4,new(24420.258f,7.9804688f,-48281.227f),new(24419.484f,7.9804688f,-48279.938f),new(-0.7726201f,0f,1.2857128f),new(-0.7726201f,0f,1.2857128f),[],[3,10]){Blinker=LaneBlinker.NoBlinker},
        new(5,new(24440.986f,7.9804688f,-48334.6f),new(24440.572f,7.9804688f,-48333.16f),new(-0.41352683f,0f,1.4418727f),new(-0.41352683f,0f,1.4418727f),[6],[]){Blinker=LaneBlinker.NoBlinker},
        new(6,new(24440.572f,7.9804688f,-48333.16f),new(24439.744f,7.9804688f,-48330.273f),new(-0.8280871f,0f,2.8873487f),new(-0.84319425f,0f,2.882973f),[10],[5]){Blinker=LaneBlinker.NoBlinker},
        new(7,new(24458.104f,7.9804688f,-48326.73f),new(24457.332f,7.9804688f,-48325.445f),new(-0.7726201f,0f,1.2857128f),new(-0.7726201f,0f,1.2857128f),[8],[]){Blinker=LaneBlinker.NoBlinker},
        new(8,new(24457.332f,7.9804688f,-48325.445f),new(24427.973f,7.9804688f,-48276.59f),new(-29.359564f,0f,48.857094f),new(-29.359564f,0f,48.857094f),[9],[7]){Blinker=LaneBlinker.NoBlinker},
        new(9,new(24427.973f,7.9804688f,-48276.59f),new(24427.2f,7.9804688f,-48275.305f),new(-0.7726201f,0f,1.2857128f),new(-0.7726201f,0f,1.2857128f),[],[8]){Blinker=LaneBlinker.NoBlinker},
        new(10,new(24439.744f,7.9804688f,-48330.273f),new(24420.258f,7.9804688f,-48281.227f),new(-14.818061f,0f,50.664562f),new(-27.189558f,0f,45.246f),[4],[6]){Blinker=LaneBlinker.NoBlinker},
        new(11,new(24454.248f,7.9804688f,-48329.05f),new(24453.475f,7.9804688f,-48327.766f),new(-0.7726201f,0f,1.2857128f),new(-0.7726201f,0f,1.2857128f),[12],[]){Blinker=LaneBlinker.NoBlinker},
        new(12,new(24453.475f,7.9804688f,-48327.766f),new(24424.115f,7.9804688f,-48278.906f),new(-29.359564f,0f,48.857094f),new(-29.359564f,0f,48.857094f),[0],[11]){Blinker=LaneBlinker.NoBlinker}]);
}
