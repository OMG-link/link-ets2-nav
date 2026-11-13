using System.Text;
using LinkEts2Nav.Map;
using TruckLib;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;
public sealed class SpeedLimitDefinitionTests
{
    private static string Format(string text) => System.Text.RegularExpressions.Regex.Replace(text.Replace("{","{\\n").Replace("}","\\n}"), @"([a-z_]+(?:\[\])?):", "\\n$1:").Replace("\\n","\n");
    private sealed class Fs : IFileSystem
    {
        public Dictionary<string,string> Files=new()
        {
            ["/def/world/traffic_rules.sii"]="""SiiNunit { traffic_rule_data : traffic_rule.limit_60 { rule: speed_limit num_params[]: 60 } }""",
            ["/def/world/traffic_lane.sii"]="""SiiNunit { traffic_lane_data : traffic_lane.road.local { speed_class: local_road } }""",
            ["/def/world/road_look.sii"]="""SiiNunit { road_look : road.un11 { lanes_left[]: traffic_lane.road.local lanes_right[]: traffic_lane.road.local } }""",
            ["/def/world/sign.test.sii"]="""SiiNunit { sign_model : sign.test { traffic_rule: traffic_rule.limit_60 } }""",
            ["/def/city.sii"]="""SiiNunit { city_data : city.test { country: norway } }""",
            ["/def/country.sii"]="""SiiNunit { country_data : country.data.norway { country_id: 35 } }""",
            ["/def/country/norway/speed_limits.sii"]="""SiiNunit { country_speed_limit : .speed_limit.truck { vehicle_speed_class: truck lane_speed_class[]: local_road limit[]: 80 urban_limit[]: 50 max_limit[]: 80 } }"""
        };
        public char DirectorySeparator=>'/';
        public bool FileExists(string p)=>Files.ContainsKey(p);
        public bool DirectoryExists(string p)=>Files.Keys.Any(k=>k.StartsWith(p.TrimEnd('/')+"/"));
        public IList<string> GetFiles(string p)=>Files.Keys.Where(k=>GetParent(k).TrimEnd('/')==p.TrimEnd('/')).ToArray();
        public byte[] ReadAllBytes(string p)=>Encoding.UTF8.GetBytes(Format(Files[p]));
        public string ReadAllText(string p)=>Format(Files[p]);
        public string ReadAllText(string p,Encoding encoding)=>Format(Files[p]);
        public Stream Open(string p)=>new MemoryStream(ReadAllBytes(p));
        public string GetParent(string p)=>p[..(p.LastIndexOf('/')+1)];
    }
    private static Fs VariantDefinitions()
    {
        var fs=new Fs();
        fs.Files["/def/world/traffic_lane.sii"]="""SiiNunit { traffic_lane_data : traffic_lane.local { speed_class: local_road } traffic_lane_data : traffic_lane.motor { speed_class: motorway } traffic_lane_data : traffic_lane.express { speed_class: expressway } }""";
        fs.Files["/def/world/road_look.sii"]="""SiiNunit { road_look : road.un11 { lane_variant_default: local_road lanes_left[]: traffic_lane.local lanes_right[]: traffic_lane.local lane_variants[]: .motor lane_variants[]: .special lane_variants[]: .express } road_template_variant : .motor { lane_variant: motorway lanes_left[]: traffic_lane.motor lanes_right[]: traffic_lane.motor } road_template_variant : .special { lane_variant: motorway model_variants[]: bus lanes_left[]: traffic_lane.express lanes_right[]: traffic_lane.express } road_template_variant : .express { lane_variant: expressway lanes_left[]: traffic_lane.express lanes_right[]: traffic_lane.express } }""";
        return fs;
    }

    [Fact]
    public void PreparedJourneyCategoryLimitUsesCountryRuralMaximumWithoutAreaOrSigns()
    {
        var fs=new Fs();
        fs.Files["/def/country/norway/speed_limits.sii"]="""SiiNunit { country_speed_limit : .speed_limit.truck { vehicle_speed_class: truck lane_speed_class[]: local_road limit[]: 90 urban_limit[]: 30 max_limit[]: 80 } }""";
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,new(0,0,0),new(1000,0,0),default);road.RoadType="un11";
        road.Node.IsCountryBorder=true;road.Node.ForwardItem=road;road.Node.ForwardCountry=35;
        var reader=new SpeedLimitDefinitionReader(fs);
        Assert.Null(reader.RoadCategoryLimit(road,true).Kph);
        reader.PrepareHost(map,TestContext.Current.CancellationToken);
        Assert.Equal(80,reader.RoadCategoryLimit(road,true).Kph);
        Assert.Equal(80,reader.RoadCategoryLimit(road,false).Kph);
    }

    [Theory]
    [InlineData("motorway","","motorway")]
    [InlineData("expressway","","expressway")]
    [InlineData("motorway","bus","expressway")]
    [InlineData("","","local_road")]
    [InlineData("missing","",null)]
    public void LaneVariantAndModelVariantSelectActualClass(string variant,string model,string? expected)
    {
        var map=new TruckLib.ScsMap.Map();
        var road=Road.Add(map,new(0,0,0),new(1000,0,0),default);road.RoadType="un11";
        road.Right.TrafficRule=variant;road.Right.Variant=model;
        var data=new SpeedLimitDefinitionReader(VariantDefinitions()).Build(map,TestContext.Current.CancellationToken);
        Assert.Null(data.Error);
        var metadata=data.Roads[road.Uid];
        Assert.Equal(expected,metadata.ClassFor(metadata.Geometry));
        Assert.Equal("local_road",metadata.ClassFor(metadata.Geometry with {
            StartNodeUid=metadata.Geometry.EndNodeUid,EndNodeUid=metadata.Geometry.StartNodeUid }));
    }

    [Fact]
    public void MissingLaneReferenceDoesNotSilentlyUseOtherLanes()
    {
        var fs=VariantDefinitions();
        fs.Files["/def/world/road_look.sii"]=fs.Files["/def/world/road_look.sii"].Replace(
            "lanes_right[]: traffic_lane.motor", "lanes_right[]: traffic_lane.motor lanes_right[]: traffic_lane.missing");
        var map=new TruckLib.ScsMap.Map();
        var road=Road.Add(map,new(0,0,0),new(1000,0,0),default);road.RoadType="un11";
        road.Right.TrafficRule="motorway";
        var reader=new SpeedLimitDefinitionReader(fs);
        var data=reader.Build(map,TestContext.Current.CancellationToken);
        Assert.Null(data.Error);
        Assert.Null(data.Roads[road.Uid].ClassFor(data.Roads[road.Uid].Geometry));
        Assert.Null(reader.RoadTrafficDirections(road).Forward);
    }

    [Fact]
    public void NearbyRoadUsesDistantHostBorderAndRebuildsForNewMap()
    {
        var host = new TruckLib.ScsMap.Map();
        var first=Road.Add(host,new(0,0,0),new(1000,0,0),default);
        var next=first.Append(new(2000,0,0));
        first.RoadType=next.RoadType="un11";
        first.Node.IsCountryBorder=true;
        first.Node.ForwardItem=first;
        first.Node.ForwardCountry=35;
        var nearby=new TruckLib.ScsMap.Map();
        nearby.MapItems.Add(next.Uid,next);
        var reader=new SpeedLimitDefinitionReader(new Fs());
        var data=reader.Build(nearby,TestContext.Current.CancellationToken,host);
        Assert.Equal("norway",data.Roads[next.Uid].Country);
        var empty=new TruckLib.ScsMap.Map();
        data=reader.Build(nearby,TestContext.Current.CancellationToken,empty);
        Assert.Null(data.Roads[next.Uid].Country);
    }

    [Fact]
    public void MissingCityCountryIsUnknownRatherThanAScanFailure()
    {
        var fs=new Fs();
        fs.Files["/def/city.sii"]="""SiiNunit { city_data : city.test { } }""";
        var map=new TruckLib.ScsMap.Map();
        var road=Road.Add(map,new(0,0,0),new(1000,0,0),default);
        road.RoadType="un11";
        CityArea.Add(map,new(500,0,0),"test",1000,1000);
        var data=new SpeedLimitDefinitionReader(fs).Build(map,TestContext.Current.CancellationToken);
        Assert.Null(data.Error);
        Assert.Null(data.Roads[road.Uid].Country);
        Assert.Null(Assert.Single(data.Regions).Country);
    }
    [Fact]
    public void CountriesInOneWindowStayBoundToTheirOwnRoadComponents()
    {
        var fs=new Fs();
        fs.Files["/def/city.sii"]="""SiiNunit { city_data : city.test { country: norway } city_data : city.other { country: estonia } }""";
        fs.Files["/def/country.sii"]="""SiiNunit { country_data : country.data.norway { country_id: 35 } country_data : country.data.estonia { country_id: 22 } }""";
        var map=new TruckLib.ScsMap.Map();
        var first=Road.Add(map,new(0,0,0),new(1000,0,0),default); first.RoadType="un11";
        var other=Road.Add(map,new(10000,0,0),new(11000,0,0),default); other.RoadType="un11";
        CityArea.Add(map,new(500,0,0),"test",1000,1000);
        CityArea.Add(map,new(10500,0,0),"other",1000,1000);
        var data=new SpeedLimitDefinitionReader(fs).Build(map,TestContext.Current.CancellationToken);
        Assert.Null(data.Country);
        Assert.Equal("norway",data.Roads[first.Uid].Country);
        Assert.Equal("estonia",data.Roads[other.Uid].Country);
    }

    [Fact]
    public void CountryIdDefinitionsResolveBorderEvidenceOutsideAnyCity()
    {
        var map=new TruckLib.ScsMap.Map();
        var road=Road.Add(map,new(0,0,0),new(1000,0,0),default); road.RoadType="un11";
        road.Node.IsCountryBorder=true;
        road.Node.ForwardItem=road;
        road.Node.ForwardCountry=35;
        var data=new SpeedLimitDefinitionReader(new Fs()).Build(map,TestContext.Current.CancellationToken);
        Assert.Equal("norway",data.Roads[road.Uid].Country);
    }

    [Fact]
    public void ParsesTokenCountryLaneClassAndSignSemantics()
    {
        var map=new TruckLib.ScsMap.Map();
        var road=Road.Add(map,new(0,0,0),new(1000,0,0),default);
        road.RoadType="un11";
        CityArea.Add(map,new(500,0,0),"test",1000,1000);
        Sign.Add(map,new(400,0,5),"test");
        var data=new SpeedLimitDefinitionReader(new Fs()).Build(map,TestContext.Current.CancellationToken);
        Assert.Null(data.Error);
        Assert.Null(data.Country);
        Assert.Equal("norway",data.Roads[road.Uid].Country);
        Assert.Equal("local_road",data.Roads[road.Uid].SpeedClass);
        Assert.Equal(50,data.Limits["norway/local_road"].Urban);
        Assert.Equal(60,Assert.Single(data.Signs).Action.SetKph);
    }

    [Fact]
    public void ActiveDirectoryModOverridesBaseTruckLimitsWithoutChangingGeometry()
    {
        string root=Path.Combine(Path.GetTempPath(),"nav-limit-mod-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,"def/country/norway"));
        try
        {
            File.WriteAllText(Path.Combine(root,"def/country/norway/speed_limits.sii"),Format(
                """SiiNunit { country_speed_limit : .speed_limit.truck { vehicle_speed_class: truck lane_speed_class[]: local_road limit[]: 70 urban_limit[]: 40 max_limit[]: 70 } }"""));
            var map=new TruckLib.ScsMap.Map();
            CityArea.Add(map,new(0,0,0),"test",1000,1000);
            var data=new SpeedLimitDefinitionReader(new Fs(),[root]).Build(map,TestContext.Current.CancellationToken);
            Assert.Null(data.Error);
            Assert.Equal(40,data.Limits["norway/local_road"].Urban);
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact]
    public void CityMembershipDoesNotSetUrbanLimitButTrafficAreaDoes()
    {
        var fs=new Fs();
        fs.Files["/def/world/traffic_rules.sii"]="""SiiNunit { traffic_rule_data : traffic_rule.urban_area { rule: lane str_params[]: urban_area str_params[]: area } traffic_rule_data : traffic_rule.city_area { additional_rules[]: traffic_rule.urban_area } }""";
        var map=new TruckLib.ScsMap.Map();
        var city=CityArea.Add(map,new(500,0,0),"test",1000,1000);
        city.Node.Rotation=System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY,.4f);
        TrafficArea.Add(map,[new(400,0,-50),new(600,0,-50),new(600,0,50),new(400,0,50)],"city_area");
        var regions=new SpeedLimitDefinitionReader(fs).ReadRegions(map);
        Assert.Equal(2,regions.Count);
        Assert.False(regions.Single(r=>r.Country=="norway").Urban);
        Assert.Single(regions,r=>r.Urban);
    }

    [Fact]
    public void ImplicitResetIsAnOperationNotAVehicleFilter()
    {
        var fs=new Fs();
        fs.Files["/def/world/traffic_rules.sii"]="""SiiNunit { traffic_rule_data : traffic_rule.impl_reset { rule: speed_limit num_params[]: 0 num_params[]: 1 str_params[]: implicit } }""";
        fs.Files["/def/world/sign.test.sii"]="""SiiNunit { sign_model : sign.test { traffic_rule: traffic_rule.impl_reset } }""";
        var map=new TruckLib.ScsMap.Map();Sign.Add(map,new(400,0,5),"test");
        var data=new SpeedLimitDefinitionReader(fs).Build(map,TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(data.Signs).Action.Reset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptySideHasNoTrafficButMissingLaneMetadataIsNotTreatedAsAClosedLane(bool leftHand)
    {
        var fs=new Fs();
        fs.Files["/def/world/road_look.sii"]="""SiiNunit { road_look : road.un11 { lanes_right[]: traffic_lane.road.local } }""";
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,new(0,0,0),new(1000,0,0),default);
        road.RoadType="un11";road.LeftHandTraffic=leftHand;
        var data=new SpeedLimitDefinitionReader(fs).Build(map,TestContext.Current.CancellationToken);
        Assert.Equal(!leftHand,data.Roads[road.Uid].ForwardTraffic);
        Assert.Equal(leftHand,data.Roads[road.Uid].ReverseTraffic);
        var reader=new SpeedLimitDefinitionReader(fs);
        Assert.Equal((!leftHand,leftHand),(reader.RoadTrafficDirections(road).Forward!.Value,reader.RoadTrafficDirections(road).Reverse!.Value));
        road.RoadType="unresolved";
        data=new SpeedLimitDefinitionReader(fs).Build(map,TestContext.Current.CancellationToken);
        Assert.True(data.Roads[road.Uid].ForwardTraffic);Assert.True(data.Roads[road.Uid].ReverseTraffic);
        Assert.Null(reader.RoadTrafficDirections(road).Forward);Assert.Null(reader.RoadTrafficDirections(road).Reverse);
    }

}
