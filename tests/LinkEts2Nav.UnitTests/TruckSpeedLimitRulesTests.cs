using LinkEts2Nav.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class TruckSpeedLimitRulesTests
{
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
