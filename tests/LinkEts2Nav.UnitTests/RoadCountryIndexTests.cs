using System.Numerics;
using LinkEts2Nav.Map;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;
public sealed class RoadCountryIndexTests
{
 private static readonly IReadOnlyDictionary<byte,string> Names=new Dictionary<byte,string>{{23,"finland"},{38,"russia"},{0,"invalid-zero"}};
 private static Road Add(TruckLib.ScsMap.Map map,byte first=23,byte last=23)
 {
  var road=Road.Add(map,Vector3.Zero,new(100,0,0),default);
  road.Node.ForwardItem=road;road.Node.ForwardCountry=first;
  road.ForwardNode.BackwardItem=road;road.ForwardNode.BackwardCountry=last;
  return road;
 }
 [Fact] public void OrdinaryNodeCountriesUseTheActualRoadSideRatherThanTravelDirectionOrBorderFlag()
 {
  var map=new TruckLib.ScsMap.Map();var road=Add(map);
  road.Node.BackwardCountry=38;road.ForwardNode.ForwardCountry=38;
  road.Node.Rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,1.7f);
  Assert.False(road.Node.IsCountryBorder);Assert.False(road.ForwardNode.IsCountryBorder);
  Assert.Equal("finland",RoadCountryIndex.Build(map,Names).QueryCountry(road.Uid).Value);
 }
 [Theory][InlineData(200,200)][InlineData(200,23)]
 public void UndefinedNonzeroCountryIdsAreNotDefaultable(int first,int last)
 {
  var map=new TruckLib.ScsMap.Map();var road=Add(map,(byte)first,(byte)last);
  var index=RoadCountryIndex.Build(map,Names);
  var error=Assert.Throws<InvalidDataException>(()=>index.QueryCountry(road.Uid));
  Assert.Contains(road.Uid.ToString("X16"),error.Message);
 }
 [Theory][InlineData(0,0)][InlineData(23,38)]
 public void LoadedWithoutUniqueCountryIsDistinctFromMissingMap(int first,int last)
 {
  var map=new TruckLib.ScsMap.Map();var road=Add(map,(byte)first,(byte)last);
  var result=RoadCountryIndex.Build(map,Names).QueryCountry(road.Uid);
  Assert.Null(result.Value);Assert.IsType<UndeterminedRoadCountry>(result.Issue);
 }
 [Theory][InlineData(0,23)][InlineData(23,0)]
 public void OneNonzeroCountryIdentifiesTheRoadWithoutGuessing(int first,int last)
 {
  var map=new TruckLib.ScsMap.Map();var road=Add(map,(byte)first,(byte)last);
  var result=RoadCountryIndex.Build(map,Names).QueryCountry(road.Uid);
  Assert.Null(result.Issue);Assert.Equal("finland",result.Value);
 }
 [Fact] public void AnUnrelatedZeroRoadDoesNotPreventPreparationOrAValidRoadQuery()
 {
  var map=new TruckLib.ScsMap.Map();var invalid=Add(map,0,0);var valid=Add(map);
  var index=RoadCountryIndex.Build(map,Names);
  Assert.Equal("finland",index.QueryCountry(valid.Uid).Value);
  Assert.IsType<UndeterminedRoadCountry>(index.QueryCountry(invalid.Uid).Issue);
 }
 [Fact] public void MissingEndpointHasALoadDependencyRatherThanAnInventedZero()
 {
  var map=new TruckLib.ScsMap.Map();var road=Add(map);ulong node=road.ForwardNode.Uid;
  var type=typeof(Road).Assembly.GetType("TruckLib.ScsMap.UnresolvedNode")!;
  road.ForwardNode=(INode)Activator.CreateInstance(type,[node])!;
  var index=RoadCountryIndex.Build(map,Names);
  var unknown=index.QueryCountry(road.Uid);
  Assert.IsType<MapQueryFailure>(unknown.Issue);Assert.Null(unknown.Value);
  var issue=new MissingMapSectors([new(3,4)],"native endpoint owner");
  var pending=index.QueryCountry(road.Uid,uid=>{Assert.Equal(node,uid);return issue;});
  Assert.Same(issue,pending.Issue);Assert.Null(pending.Value);
 }
 [Fact] public void NullSerializedEndpointAndUnmatchedItemSidesAreInvalid()
 {
  var map=new TruckLib.ScsMap.Map();var road=Add(map);road.Node.ForwardItem=null!;
  Assert.Throws<InvalidDataException>(()=>RoadCountryIndex.Build(map,Names).QueryCountry(road.Uid));
  road.Node=null!;
  Assert.Throws<InvalidDataException>(()=>RoadCountryIndex.Build(map,Names).QueryCountry(road.Uid));
 }
 [Fact] public void IndexOwnsImmutableEndpointEvidenceAndDoesNotTraverseUnrelatedNodes()
 {
  var map=new TruckLib.ScsMap.Map();var road=Add(map);var index=RoadCountryIndex.Build(map,Names);
  road.Node.ForwardCountry=38;map.MapItems.Clear();
  Assert.Equal("finland",index.QueryCountry(road.Uid).Value);Assert.False(index.Contains(999));
  Assert.IsType<MapQueryFailure>(index.QueryCountry(999).Issue);
 }
 [Fact] public void CancelledBuildDoesNotReturnAnIndex()
 {
  using var stop=new CancellationTokenSource();stop.Cancel();
  Assert.Throws<OperationCanceledException>(()=>RoadCountryIndex.Build(new(),Names,stop.Token));
 }
}
