using LinkEts2Nav.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class TruckSpeedLimitRulesTests
{
    [Theory]
    [InlineData("motorway",70,50,70)]
    [InlineData("expressway",70,40,70)]
    [InlineData("local_road",60,40,60)]
    [InlineData("slow_road",30,30,30)]
    public void LoadedButUndeterminedCountryUsesTheSharedKnownClassTable(string speedClass,int rural,int urban,int maximum)
    {
        Assert.True(TruckSpeedLimitRules.TryGet(new Dictionary<string,TruckSpeedLimit>(),null,speedClass,out var rule,out bool usedDefault));
        Assert.True(usedDefault);Assert.Equal(new(rural,urban,maximum),rule);
        Assert.Contains(speedClass,TruckSpeedLimitRules.DefaultReasonFor(null,speedClass));
    }
    [Fact] public void UndeterminedCountryDoesNotInventAnUnknownClass()
        =>Assert.False(TruckSpeedLimitRules.TryGet(new Dictionary<string,TruckSpeedLimit>(),null,"unknown",out _,out _));

    [Fact]
    public void MissingSlowRoadUsesExplicitThirtyDefault()
    {
        Assert.True(TruckSpeedLimitRules.TryGet(new Dictionary<string,TruckSpeedLimit>(),"norway","slow_road",out var rule,out bool fallback));
        Assert.Equal(new(30,30,30),rule);Assert.True(fallback);
    }
    [Theory]
    [InlineData("norway","local_road")]
    [InlineData("norway","unknown")]
    [InlineData("","slow_road")]
    public void OtherUnknownRulesAndUnknownCountryStayUnknown(string country,string speedClass)
    {
        Assert.False(TruckSpeedLimitRules.TryGet(new Dictionary<string,TruckSpeedLimit>(),country,speedClass,out _,out bool fallback));
        Assert.False(fallback);
    }
    [Theory]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(0)]
    public void AnExistingCountryEntryAlwaysWins(int limit)
    {
        var defined=new TruckSpeedLimit(limit,limit,limit);
        Assert.True(TruckSpeedLimitRules.TryGet(new Dictionary<string,TruckSpeedLimit>{{"test/slow_road",defined}},"test","slow_road",out var rule,out bool fallback));
        Assert.Same(defined,rule);Assert.False(fallback);
    }
}
