using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Map;
namespace LinkEts2Nav.UnitTests;
public sealed class NarrowRoadTests
{
    private static DirectedRoad Edge(ulong id,ulong from,ulong to,float a,float b)=>new(id.ToString("X16"),from,to,[new(a,0,0),new(b,0,0)]);
    private static readonly DirectedRoad Approach=Edge(1,1,2,0,600),First=Edge(2,2,3,600,625),Second=Edge(3,3,4,625,650),After=Edge(4,4,5,650,1000);
    private static SpeedLimitData Data()=>new(new Dictionary<ulong,LimitRoad>{{2,new(First,"local_road"){SingleTrack=true}},{3,new(Second,"local_road"){SingleTrack=true}}},[],[],new Dictionary<string,TruckSpeedLimit>(),null);
    [Fact]
    public void AdjacentRoadItemsAreOneSeriesWith500And200PreviewsAndExitEnd()
    {
        var e=Assert.Single(NarrowRoadEventBuilder.Build([Approach,First,Second,After],Data()));
        Assert.Equal(600,e.AlongMeters);Assert.Equal(650,e.EndAlongMeters);
        var scheduler=new SpeechTriggerScheduler();
        var plan=new EnvironmentPreviewPlanner().Build(NarrowRoadEventBuilder.Kind,0,[e],scheduler,true);
        Assert.Equal(new[]{100d,400d},plan.Triggers.Select(t=>t.AlongMeters));
        Assert.Equal(new[]{"五百米后，进入窄路，请减速观察。","两百米后，进入窄路，请减速观察。"},plan.Triggers.Select(t=>t.Text));
        Assert.Equal(650,Assert.IsType<RouteSpeechEnd>(Assert.Single(plan.Sequences).End).AlongMeters);
    }
    [Fact]
    public void TrimmingTheFirstHalfDoesNotInventANewSeriesOrMoveItsEntry()
    {
        var full=Assert.Single(NarrowRoadEventBuilder.Build([Approach,First,Second,After],Data()));
        var trimmed=Assert.Single(NarrowRoadEventBuilder.Build([Second,After],Data(),625));
        Assert.Equal(full.Id,trimmed.Id);Assert.Equal(full.AlongMeters,trimmed.AlongMeters);
        Assert.Equal(full.EndAlongMeters,trimmed.EndAlongMeters);
    }
    [Fact]
    public void LateDiscoveryRoundsDownToFiftyAndSkipsTwoHundred()
    {
        var e=Assert.Single(NarrowRoadEventBuilder.Build([Approach,First,Second],Data()));
        var plan=new EnvironmentPreviewPlanner().Build("narrow",327,[e],new SpeechTriggerScheduler(),true);
        Assert.Equal("两百五十米后，进入窄路，请减速观察。",Assert.Single(plan.Triggers).Text);
    }
    [Fact]
    public void ReverseTraversalAlsoSeesOneContiguousSection()
    {
        static DirectedRoad Reverse(DirectedRoad r)=>r with{StartNodeUid=r.EndNodeUid,EndNodeUid=r.StartNodeUid,Points=r.Points.Reverse().ToArray()};
        var e=Assert.Single(NarrowRoadEventBuilder.Build([Reverse(After),Reverse(Second),Reverse(First),Reverse(Approach)],Data()));
        Assert.Equal(350,e.AlongMeters);Assert.Equal(400,e.EndAlongMeters);
    }
    [Fact]
    public void NameOrGeometryAloneDoesNotCreateANarrowRoadEvent()
    {
        Assert.Empty(NarrowRoadEventBuilder.Build([First with{RoadType="un11_onelane"}],Data() with{Roads=new Dictionary<ulong,LimitRoad>{{2,new(First,"local_road")}}}));
        Assert.Empty(NarrowRoadEventBuilder.Build([First with{StartNodeUid=99}],Data()));
    }
    [Fact]
    public void SeparateSectionsRemainSeparateAndCanonicalCoordinatesArePreserved()
    {
        var gap=Edge(9,3,2,625,700);
        var events=NarrowRoadEventBuilder.Build([Approach,First,gap,First],Data(),1000);
        Assert.Equal(2,events.Count);Assert.NotEqual(events[0].Id,events[1].Id);
        Assert.Equal(1600,events[0].AlongMeters);
    }
}
