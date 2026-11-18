using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Speech;
namespace LinkEts2Nav.UnitTests;
public sealed class NaturalEntrySpeechTests
{
    [Fact]
    public void DirectionlessMergeUsesTheOrdinaryNavigationStages()
    {
        var merge=new NavigationManeuver("natural",ManeuverKind.Merge,1000,"in","out")
            {EndDistanceMeters=1050,JunctionKind=NavigationJunctionKind.Ramp};
        var plan=new NavigationEventPlanner().Build("s","r",DateTimeOffset.UtcNow,0,[merge],true,false,false,new());
        Assert.Single(plan.Sequences);
        foreach(int stage in new[]{1000,500,200,100})
            Assert.Contains(plan.Triggers,t=>t.AlongMeters==1000-stage
                &&t.Text==NavigationPrompts.AtDistance(ManeuverKind.Merge,stage));
        var execute=Assert.Single(plan.Triggers,t=>t.Kind==SpeechTriggerKind.Execution);
        Assert.Equal(980,execute.AlongMeters);
        Assert.Equal(NavigationPrompts.Execute(ManeuverKind.Merge),execute.Text);
        Assert.Equal(1050,plan.Sequences[0].EndAlongMeters);
        Assert.Equal(NavigationSide.None,NavigationManeuverDirections.Side(merge));
    }
    [Fact]
    public void DefaultPackManifestIncludesGenericMergePhrases()
    {
        foreach(int meters in Enumerable.Range(1,10).Select(n=>n*50).Concat(new[]{1000,2000}))
            Assert.Contains(NavigationPrompts.AtDistance(ManeuverKind.Merge,meters),SpeechTextCatalog.DefaultTexts);
        Assert.Contains(NavigationPrompts.Immediate(ManeuverKind.Merge),SpeechTextCatalog.DefaultTexts);
        Assert.Contains(NavigationPrompts.Execute(ManeuverKind.Merge),SpeechTextCatalog.DefaultTexts);
    }
}
