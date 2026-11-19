using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;
public sealed class UndeterminedCountryLimitTests
{
 private static SpeedLimitResult Resolve(string speedClass,bool urban=false,int? posted=null,int? area=null,
     Func<MapQueryResult<string>>? countryQuery=null)
 {
  var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,Vector3.Zero,new(1000,0,0),default);
  var geometry=new DirectedRoad(road.Uid.ToString("X16"),road.Node.Uid,road.ForwardNode.Uid,[Vector3.Zero,new(1000,0,0)]);
  var index=RoadCountryIndex.Build(map,new Dictionary<byte,string>());
  var metadata=new LimitRoad(geometry,speedClass){CountryQuery=countryQuery??(()=>index.QueryCountry(road.Uid))};
  LimitRegion[] regions=urban||area.HasValue?[new("area",[new(600,0,-10),new(800,0,-10),new(800,0,10),new(600,0,10)],0,urban,area)]:[];
  LimitSign[] signs=posted.HasValue?[new("posted",new(200,0,1),Vector3.UnitX,false,new(SetKph:posted))]:[];
  var data=new SpeedLimitData(new Dictionary<ulong,LimitRoad>{{road.Uid,metadata}},signs,regions,
      new Dictionary<string,TruckSpeedLimit>(),null);
  var resolver=new SpeedLimitResolver(ProjectedSignBuilder.Build(data,[]),new RoadPathSource(map));
  return resolver.ResolveOnPath(Vector3.Zero,Vector3.UnitX,100,new("camera",new(700,0,0),Vector3.UnitX),[geometry]);
 }
 [Theory]
 [InlineData("motorway",false,null,null,70)]
 [InlineData("motorway",true,null,null,50)]
 [InlineData("expressway",false,null,null,70)]
 [InlineData("expressway",true,null,null,40)]
 [InlineData("local_road",false,null,null,60)]
 [InlineData("local_road",true,null,null,40)]
 [InlineData("slow_road",false,null,null,30)]
 [InlineData("motorway",false,50,null,50)]
 [InlineData("motorway",false,90,null,70)]
 [InlineData("motorway",false,null,30,30)]
 public void DefaultIsStillCombinedWithCitySignsAndArea(string cls,bool urban,int? posted,int? area,int expected)
 {
  var result=Resolve(cls,urban,posted,area);Assert.Equal(expected,result.Kph);
  Assert.Contains(TruckSpeedLimitRules.DefaultReasonFor(null,cls),result.Reason);
 }
 [Fact] public void ATrueMapDependencyDoesNotBecomeAnUnknownCountryDefault()
 {
  var issue=new MissingMapObjects([new(MapObjectKind.Node,12)],"unloaded native endpoint");
  var result=Resolve("motorway",countryQuery:()=>MapQueryResult<string>.Unavailable(issue));
  Assert.Null(result.Kph);Assert.IsType<MissingMapObjects>(result.MapIssue);
 }
 [Fact] public void UnknownClassAndKnownCountryMissingClassDoNotDefault()
 {
  Assert.Null(Resolve("unknown").Kph);
  Assert.Null(Resolve("local_road",countryQuery:()=>MapQueryResult<string>.Ready("known")).Kph);
  Assert.Equal(30,Resolve("slow_road",countryQuery:()=>MapQueryResult<string>.Ready("known")).Kph);
 }
}
