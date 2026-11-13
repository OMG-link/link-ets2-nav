using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Map;
namespace LinkEts2Nav.UnitTests;

public sealed class CityEntryEventBuilderTests
{
    private static DirectedRoad Road(params Vector3[] points)=>new("route",1,2,points);
    private static LimitRegion Urban(string id,float from,float to,float height=0)
        =>new(id,[new(from,0,-10),new(to,0,-10),new(to,0,10),new(from,0,10)],height,true);
    private static IReadOnlyList<EnvironmentPreviewEvent> Events(LimitRegion[] areas,double player=0)
        =>CityEntryEventBuilder.Build([Road(new(0,0,0),new(1000,0,0))],player,areas);

    [Fact] public void DetectsDirectedEntryAndUsesExistingFiveHundredTwoHundredPlanner()
    {
        var e=Assert.Single(Events([Urban("city",600,800)]));
        Assert.Equal(600,e.AlongMeters);Assert.Equal(600,e.EndAlongMeters);Assert.Equal("city-entry",e.Kind);
        var plan=new EnvironmentPreviewPlanner().Build("city",0,[e],new SpeechTriggerScheduler(),true);
        Assert.Equal(new[]{100d,400d},plan.Triggers.Select(t=>t.AlongMeters));
        Assert.Equal(new[]{"五百米后，进入城区，请注意减速慢行。","两百米后，进入城区，请注意减速慢行。"},plan.Triggers.Select(t=>t.Text));
    }
    [Fact] public void OppositeTravelUsesOppositeBoundaryAndNoExitAnnouncement()
    {
        var forward=Assert.Single(Events([Urban("city",400,700)]));
        var reverse=Assert.Single(CityEntryEventBuilder.Build([Road(new(1000,0,0),new(0,0,0))],0,[Urban("city",400,700)]));
        Assert.Equal(400,forward.AlongMeters);Assert.Equal(300,reverse.AlongMeters);Assert.NotEqual(forward.Id,reverse.Id);
    }
    [Fact] public void AlreadyInsideSkipsCurrentCityButStillFindsLaterEntry()
    {
        var e=Assert.Single(Events([Urban("one",400,600),Urban("two",800,900)],500));
        Assert.Equal(800,e.AlongMeters);
        var beginsInside=Assert.Single(CityEntryEventBuilder.Build([Road(new(450,0,0),new(1000,0,0))],0,
            [Urban("one",400,600),Urban("two",800,900)]));
        Assert.Equal(350,beginsInside.AlongMeters);
    }
    [Theory]
    [InlineData(550)]
    [InlineData(600)]
    public void OverlappingOrTouchingUrbanAreasHaveOnlyOneEntry(float secondStart)
        =>Assert.Equal(400,Assert.Single(Events([Urban("one",400,600),Urban("two",secondStart,800)])).AlongMeters);

    [Fact] public void LaterSameKindCityWaitsForPreviousEntryAndUsesShortFirstProfile()
    {
        var events=Events([Urban("one",400,500),Urban("two",700,800)]);
        Assert.Equal(2,events.Count);
        var plan=new EnvironmentPreviewPlanner().Build("city",0,events,new SpeechTriggerScheduler(),true);
        var later=Assert.Single(plan.Triggers,t=>t.EventId==events[1].Id);
        Assert.Equal(400,later.AlongMeters);Assert.True(later.StrictCrossing);
        Assert.Equal("三百米后，进入城区，请注意减速慢行。",later.Text);
    }
    [Fact] public void ReenteringSameAreaProducesDistinctRouteOccurrences()
    {
        var events=CityEntryEventBuilder.Build([Road(new(0,0,0),new(1000,0,0),new(0,0,0),new(1000,0,0))],0,[Urban("same",400,600)]);
        Assert.Equal(new[]{400d,1400d,2400d},events.Select(e=>e.AlongMeters));
        Assert.Equal(3,events.Select(e=>e.Id).Distinct().Count());
    }
    [Fact] public void HeightAndExplicitUrbanRuleAreRespected()
    {
        Assert.Empty(CityEntryEventBuilder.Build([Road(new(0,20,0),new(1000,20,0))],0,[Urban("below",400,600,10)]));
        Assert.Empty(Events([Urban("city-area-only",400,600) with{Urban=false,Country="test"}]));
        Assert.Single(Events([Urban("known-urban-unknown-other-rule",400,600) with{Unsupported=true}]));
    }
    [Fact] public void EnteringUrbanHeightVolumeIsClippedAtTheActualBoundary()
    {
        var entry=Assert.Single(CityEntryEventBuilder.Build([Road(new(450,20,0),new(550,0,0))],0,[Urban("city",400,600,10)]));
        Assert.Equal(Math.Sqrt(100*100+20*20)/2,entry.AlongMeters,4);
        Assert.Equal(entry.AlongMeters,entry.EndAlongMeters);
    }
    [Fact] public void TangencyAndTravelAlongPolygonBoundaryAreNotEntry()
    {
        Assert.Empty(CityEntryEventBuilder.Build([Road(new(0,0,-20),new(400,0,-10),new(0,0,0))],0,[Urban("city",400,600)]));
        Assert.Empty(CityEntryEventBuilder.Build([Road(new(0,0,-10),new(1000,0,-10))],0,[Urban("city",400,600)]));
    }
    [Fact] public void EntryAtPolylineVertexAndTrimmedPrefixKeepSameCanonicalIdentity()
    {
        var full=Assert.Single(Events([Urban("city",400,600)],350));
        var vertex=Assert.Single(CityEntryEventBuilder.Build([Road(new(0,0,0),new(400,0,0),new(1000,0,0))],350,[Urban("city",400,600)]));
        var trimmed=Assert.Single(CityEntryEventBuilder.Build([Road(new(300,0,0),new(1000,0,0))],350,[Urban("city",400,600)],300));
        Assert.Equal(full.Id,vertex.Id);Assert.Equal(full.Id,trimmed.Id);
    }
    [Fact] public void ConcaveCityCanBeLeftAndEnteredAgain()
    {
        var u=new LimitRegion("concave",[new(400,0,0),new(800,0,0),new(800,0,100),new(700,0,100),
            new(700,0,20),new(500,0,20),new(500,0,100),new(400,0,100)],0,true);
        var e=CityEntryEventBuilder.Build([Road(new(0,0,50),new(1000,0,50))],0,[u]);
        Assert.Equal(new[]{400d,700d},e.Select(x=>x.AlongMeters));
    }
    [Fact] public void UnknownRouteGapDoesNotInventBoundaryCrossing()
    {
        var e=CityEntryEventBuilder.Build([Road(new(0,0,0),new(300,0,0)),Road(new(500,0,0),new(1000,0,0))],0,[Urban("city",400,600)]);
        Assert.Empty(e);
    }
    [Theory]
    [InlineData(450,"四百五十米后，进入城区，请注意减速慢行。")]
    [InlineData(225,"两百米后，进入城区，请注意减速慢行。")]
    [InlineData(25,"即将进入城区，请注意减速慢行。")]
    public void LateEntryFloorsToFiftyAndDoesNotKeepTwoHundred(double distance,string text)
    {
        var e=Assert.Single(Events([Urban("city",600,800)],600-distance));
        var plan=new EnvironmentPreviewPlanner().Build("city",600-distance,[e],new SpeechTriggerScheduler(),true);
        Assert.Equal(text,Assert.Single(plan.Triggers).Text);
    }
}
