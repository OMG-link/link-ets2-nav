using LinkEts2Nav.Core;
using System.Text.Json;

namespace LinkEts2Nav.UnitTests;

public sealed class MergeBlinkerDirectionTests
{
    private static PrefabLanePath Path(params LaneBlinker[] lamps)
        =>new(42,10,20,0,0,0,0,2,1,[0,1,9],[],30){CurveBlinkers=lamps};

    [Theory]
    [InlineData(LaneBlinker.Left,true)]
    [InlineData(LaneBlinker.Right,false)]
    public void SingleIncomingLampSelectsTheOtherUnsignalledPath(LaneBlinker lamp,bool moveLeft)
    {
        var ending=Path(LaneBlinker.NoBlinker,lamp,LaneBlinker.NoBlinker);
        var main=Path(LaneBlinker.NoBlinker,LaneBlinker.NoBlinker,LaneBlinker.NoBlinker);
        var decision=MergeBlinkerDirection.Classify(ending,main,1);
        Assert.True(decision.FirstEnds);Assert.Equal(moveLeft,decision.MoveLeft);
        var reversed=MergeBlinkerDirection.Classify(main,ending,1);
        Assert.False(reversed.FirstEnds);Assert.Equal(moveLeft,reversed.MoveLeft);
    }

    [Theory]
    [InlineData(LaneBlinker.NoBlinker,LaneBlinker.NoBlinker)]
    [InlineData(LaneBlinker.Left,LaneBlinker.Right)]
    [InlineData(LaneBlinker.Left,LaneBlinker.Left)]
    [InlineData(LaneBlinker.Right,LaneBlinker.Right)]
    [InlineData((LaneBlinker)6,LaneBlinker.NoBlinker)]
    public void MissingOrCompetingCommandsRemainUnknown(LaneBlinker a,LaneBlinker b)
    {
        var decision=MergeBlinkerDirection.Classify(Path(LaneBlinker.NoBlinker,a,LaneBlinker.NoBlinker),
            Path(LaneBlinker.NoBlinker,b,LaneBlinker.NoBlinker),1);
        Assert.Null(decision.FirstEnds);Assert.Null(decision.MoveLeft);
    }

    [Fact]
    public void EarlierTurnAndDownstreamLampDoNotLeakIntoMergeDecision()
    {
        var decision=MergeBlinkerDirection.Classify(Path(LaneBlinker.Right,LaneBlinker.NoBlinker,LaneBlinker.Left),
            Path(LaneBlinker.NoBlinker,LaneBlinker.NoBlinker,LaneBlinker.NoBlinker),1);
        Assert.Null(decision.FirstEnds);
    }

    [Fact]
    public void MissingNativeMetadataIsNotEquivalentToNoBlinker()
    {
        var missing=Path();
        Assert.Null(MergeBlinkerDirection.Classify(missing,Path(LaneBlinker.NoBlinker,LaneBlinker.Right,LaneBlinker.NoBlinker),1).FirstEnds);
    }

    [Fact]
    public void ForcedNoBlinkerIsAnUnsignalledContinuingPath()
    {
        Assert.True(MergeBlinkerDirection.Classify(Path(LaneBlinker.NoBlinker,LaneBlinker.Left,LaneBlinker.NoBlinker),
            Path(LaneBlinker.NoBlinker,LaneBlinker.NoBlinkerForced,LaneBlinker.NoBlinker),1).FirstEnds);
    }

    [Fact]
    public void SavedNativeMergeFactsHaveOneSignalledAndOneContinuingApproach()
    {
        // Only native curve indices and lamp values are retained, not game mesh assets.
        using var stream=typeof(MergeBlinkerDirectionTests).Assembly.GetManifestResourceStream("MergeBlinkerFacts.json")!;
        using var doc=JsonDocument.Parse(stream);
        Assert.Equal(12,doc.RootElement.GetArrayLength());
        foreach(var sample in doc.RootElement.EnumerateArray())
        {
            PrefabLanePath Read(string prefix)=>Path() with
            {
                CurveIndices=sample.GetProperty(prefix+"Curves").EnumerateArray().Select(c=>c.GetInt32()).ToArray(),
                CurveBlinkers=sample.GetProperty(prefix+"Lamps").EnumerateArray().Select(c=>Enum.Parse<LaneBlinker>(c.GetString()!)).ToArray()
            };
            var result=MergeBlinkerDirection.Classify(Read("first"),Read("second"),sample.GetProperty("sharedSuffixCount").GetInt32());
            Assert.Equal(sample.GetProperty("firstEnds").GetBoolean(),result.FirstEnds);
            Assert.Equal(sample.GetProperty("moveLeft").GetBoolean(),result.MoveLeft);
        }
    }
}
