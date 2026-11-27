using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib;
using TruckLib.ScsMap;
using ControlNode = TruckLib.Models.Ppd.ControlNode;
using NavCurve = TruckLib.Models.Ppd.NavCurve;
using PrefabDescriptor = TruckLib.Models.Ppd.PrefabDescriptor;
namespace LinkEts2Nav.UnitTests;

public sealed class GameDataManagerTests
{
    private const ulong OverridePrefab = 0x4F84385F6C870001;
    private const string OverrideResource = "/prefab2/fork_temp/invis/invis_r1_mage_left_tmpl.ppd";
    private const string TestPpd = "/prefab/test.ppd";

    [Theory]
    [InlineData("next-index", "invalid NextLines index 5")]
    [InlineData("previous-index", "invalid PreviousLines index -1")]
    [InlineData("next-reciprocity", "NextLines index 1 is not reciprocal")]
    [InlineData("previous-reciprocity", "PreviousLines index 0 is not reciprocal")]
    [InlineData("input-index", "control node 0 has invalid input curve index -2")]
    [InlineData("output-index", "control node 0 has invalid output curve index 2")]
    [InlineData("negative-length", "invalid length")]
    [InlineData("zero-length", "invalid length")]
    [InlineData("submillimeter-length", "invalid length")]
    [InlineData("nan-length", "invalid length")]
    [InlineData("infinite-length", "invalid length")]
    [InlineData("position", "nonfinite endpoint")]
    [InlineData("rotation", "nonfinite endpoint rotation")]
    [InlineData("blinker", "unknown Blinker value 3")]
    [InlineData("control-position", "control node 0 has a nonfinite position or direction")]
    [InlineData("control-direction", "control node 0 has a nonfinite position or direction")]
    public void RejectsInvalidNativeNavigationAtTheResourceBoundary(string invalidField, string diagnostic)
    {
        var native = NativePpd();
        var first = native.NavCurves[0];
        var last = native.NavCurves[1];
        switch (invalidField)
        {
            case "next-index": first.NextLines[0] = 5; break;
            case "previous-index": first.PreviousLines.Add(-1); break;
            case "next-reciprocity": last.PreviousLines.Clear(); break;
            case "previous-reciprocity": first.NextLines.Clear(); break;
            case "input-index": native.Nodes[0].InputLines[0] = -2; break;
            case "output-index": native.Nodes[0].OutputLines[0] = 2; break;
            case "negative-length": first.Length = -1; break;
            case "zero-length": first.Length = 0; break;
            case "submillimeter-length": first.Length = MathF.BitDecrement(0.001f); break;
            case "nan-length": first.Length = float.NaN; break;
            case "infinite-length": first.Length = float.PositiveInfinity; break;
            case "position": first.EndPosition = new(0, float.NaN, 0); break;
            case "rotation": first.EndRotation = new(0, 0, 0, float.PositiveInfinity); break;
            case "blinker": first.Blinker = (TruckLib.Models.Ppd.BlinkerType)3; break;
            case "control-position": native.Nodes[0].Position = new(0, float.NaN, 0); break;
            case "control-direction": native.Nodes[0].Direction = new(0, float.PositiveInfinity, 0); break;
        }
        var files = new Files();
        files.BinaryContent[TestPpd] = SerializePpd(native);
        using var data = new GameDataManager(files);

        var failure = Assert.Throws<InvalidDataException>(() => data.GetPrefabDescription(TestPpd));

        Assert.Contains(TestPpd, failure.Message);
        Assert.Contains(diagnostic, failure.Message);
    }

    [Fact]
    public void NativeResourceCacheAcceptsPortPaddingMinimumLengthAndReciprocalCycles()
    {
        var native = NativePpd();
        native.NavCurves[0].Length = 0.001f;
        native.NavCurves[0].PreviousLines.Add(1);
        native.NavCurves[1].NextLines.Add(0);
        var files = new Files();
        files.BinaryContent[TestPpd] = SerializePpd(native);
        using var data = new GameDataManager(files);

        var descriptor = data.GetPrefabDescription(TestPpd);

        Assert.Same(descriptor, data.GetPrefabDescription("/prefab/./test.ppd"));
        Assert.Equal(1, files.Reads[TestPpd]);
        Assert.Contains(-1, descriptor.Nodes[0].InputLines);
        Assert.Equal(0.001f, descriptor.NavCurves[0].Length);
        Assert.Equal([0], descriptor.NavCurves[1].NextLines);
    }

    [Theory]
    [InlineData("count", "instance has")]
    [InlineData("origin", "origin")]
    [InlineData("duplicate", "multiple slots")]
    [InlineData("position", "nonfinite position or rotation")]
    [InlineData("rotation", "nonfinite position or rotation")]
    public void ValidatesTheSourcePrefabPairAtTheInstanceBoundary(string invalidField, string diagnostic)
    {
        var native = NativePpd();
        native.Nodes.Add(new ControlNode
        {
            Position = Vector3.UnitZ, Direction = Vector3.UnitZ,
            InputLines = [-1, -1, -1, -1, -1, -1, -1, -1],
            OutputLines = [-1, -1, -1, -1, -1, -1, -1, -1]
        });
        var first = new Node { Uid = 10 };
        var last = new Node { Uid = 20, Position = Vector3.UnitZ };
        var prefab = new Prefab { Uid = 42, Nodes = [first, last] };
        switch (invalidField)
        {
            case "count": prefab.Nodes.RemoveAt(1); break;
            case "origin": typeof(Prefab).GetProperty(nameof(Prefab.Origin))!.SetValue(prefab, (ushort)2); break;
            case "duplicate": last.Uid = first.Uid; break;
            case "position": last.Position = new(0, float.PositiveInfinity, 0); break;
            case "rotation": last.Rotation = new(0, 0, 0, float.NaN); break;
        }
        var files = new Files();
        files.BinaryContent[TestPpd] = SerializePpd(native);
        using var data = new GameDataManager(files);

        var failure = Assert.Throws<InvalidDataException>(() => data.GetPrefabNavigationDescription(prefab, TestPpd));

        Assert.Contains("000000000000002A", failure.Message);
        Assert.Contains(diagnostic, failure.Message);
    }

    private static PrefabDescriptor NativePpd() => new()
    {
        Nodes =
        [
            new()
            {
                Direction = Vector3.UnitZ,
                InputLines = [0, -1, -1, -1, -1, -1, -1, -1],
                OutputLines = [1, -1, -1, -1, -1, -1, -1, -1]
            }
        ],
        NavCurves =
        [
            new() { Length = 10, StartRotation = Quaternion.Identity, EndRotation = Quaternion.Identity, NextLines = { 1 } },
            new() { Length = 10, StartRotation = Quaternion.Identity, EndRotation = Quaternion.Identity, PreviousLines = { 0 } }
        ]
    };

    private static byte[] SerializePpd(PrefabDescriptor descriptor)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        descriptor.Serialize(writer);
        return bytes.ToArray();
    }

    [Fact]
    public void InstanceNavigationOverridePreservesTheSharedNativeDescriptorAndOriginalPassages()
    {
        var (data, prefab, other, native) = OverrideScene();
        using (data)
        {
            var effective = data.GetPrefab(prefab.Uid)!.Description!;
            Assert.NotSame(native, effective);
            Assert.Same(effective, data.GetPrefabNavigationDescription(prefab, OverrideResource));
            Assert.Same(native, data.GetPrefabDescription(OverrideResource));
            Assert.Same(native, data.GetPrefab(other.Uid)!.Description);
            Assert.All(native.NavCurves.Zip(effective.NavCurves), pair => Assert.Same(pair.First, pair.Second));
            Assert.Same(native.MapPoints, effective.MapPoints);
            Assert.Same(native.Intersections, effective.Intersections);
            Assert.Equal(-1, native.Nodes[0].InputLines[1]);
            Assert.Equal(-1, native.Nodes[2].OutputLines[1]);
            Assert.Equal(6, effective.Nodes[0].InputLines[1]);
            Assert.Equal(6, effective.Nodes[2].OutputLines[1]);
            var added = Assert.Single(effective.NavCurves.Skip(native.NavCurves.Count));
            Assert.Empty(added.PreviousLines);
            Assert.Empty(added.NextLines);
            Assert.Equal(-1, added.SemaphoreId);
            Assert.Equal(new Vector3(2.25f, 0, 0), added.StartPosition);
            Assert.Equal(new Vector3(2.25f, 0, -15), added.EndPosition);
            Assert.Equal(15, added.Length);
            var passages = PrefabPathGeometry.Build(prefab, effective);
            var main = passages.Single(e => e.StartNodeUid == prefab.Nodes[0].Uid);
            var ramp = passages.Single(e => e.StartNodeUid == prefab.Nodes[1].Uid);
            Assert.Equal(new[] { 0, 3, 5 }, main.LanePaths.Single(l => l.EntryOrder == 0).CurveIndices);
            var right = main.LanePaths.Single(l => l.EntryOrder == 1);
            Assert.Equal(1, right.ExitOrder);
            Assert.Equal(new[] { 6 }, right.CurveIndices);
            Assert.Equal(new[] { 1, 4, 2, 5 }, Assert.Single(ramp.LanePaths).CurveIndices);
            Assert.Equal(2, data.GetPrefabPassageLengths(prefab.Uid, main.StartNodeUid, main.EndNodeUid).Count);
        }
    }

    [Fact]
    public void InstanceNavigationOverrideRejectsChangedIdentityEvenAfterItsDescriptorWasCached()
    {
        var (data, prefab, _, _) = OverrideScene();
        using (data)
        {
            Assert.NotNull(data.GetPrefab(prefab.Uid)!.Description);
            Assert.Throws<InvalidDataException>(() => data.GetPrefabNavigationDescription(prefab, "/prefab/other.ppd"));
            prefab.Nodes[2].Uid++;
            var failure = Assert.Throws<InvalidDataException>(() => data.GetPrefab(prefab.Uid)!.Description);
            Assert.Contains("control node UIDs", failure.Message);
        }
    }

    [Fact]
    public void InstanceNavigationOverrideRejectsChangedNativeConnectivity()
    {
        var (data, prefab, _, native) = OverrideScene();
        using (data)
        {
            native.NavCurves[0].NextLines.Clear();
            var failure = Assert.Throws<InvalidDataException>(() => data.GetPrefab(prefab.Uid)!.Description);
            Assert.Contains("connectivity changed at curve 0", failure.Message);
        }
    }

    private static (GameDataManager Data, Prefab Prefab, Prefab Other, PrefabDescriptor Native) OverrideScene()
    {
        var fs = new Files();
        fs.Content["/def/world/prefab.test.sii"] =
            $"SiiNunit {{ prefab_model : prefab.test {{ prefab_desc: \"{OverrideResource}\" }} }}";
        var map = new TruckLib.ScsMap.Map();
        Vector3[] positions = [Vector3.Zero, new(-8, 0, -1.5f), new(0, 0, -15)];
        Prefab Add(ulong uid, ulong[] controls)
        {
            var prefab = new Prefab
            {
                Uid = uid, Model = new Token("test"),
                Nodes = controls.Select((id, i) => new Node { Uid = id, Position = positions[i] }).Cast<INode>().ToList()
            };
            map.MapItems.Add(uid, prefab);
            foreach (var node in prefab.Nodes.Cast<Node>())
            {
                node.ForwardItem = prefab;
                map.Nodes.Add(node.Uid, node);
            }
            return prefab;
        }
        var prefab = Add(OverridePrefab, [0x4F84385E34070008, 0x4F84385F83070009, 0x4F84385D1F87000A]);
        var other = Add(42, [10, 20, 30]);
        var data = new GameDataManager(fs, map: map);
        var native = data.Projection("ppd", OverrideResource, () => new PrefabDescriptor
        {
            Nodes = [Control(0, 0, -1), Control(1, 1, -1), Control(2, -1, 5)],
            NavCurves = [
                Curve(new(-2.25f, 0, 0), new(-2.25f, 0, -1), [3], []),
                Curve(new(-8, 0, -1.5f), new(-7, 0, -3), [4], []),
                Curve(new(-5, 0, -5), new(-2.25f, 0, -14), [5], [4]),
                Curve(new(-2.25f, 0, -1), new(-2.25f, 0, -14), [5], [0]),
                Curve(new(-7, 0, -3), new(-5, 0, -5), [2], [1]),
                Curve(new(-2.25f, 0, -14), new(-2.25f, 0, -15), [], [2, 3])
            ]
        });
        // PPD-declared mileage can differ from endpoint distance, as it does
        // for the original terminal curve. Keep the two lengths distinguishable.
        native.NavCurves[5].Length = 1.05f;
        return (data, prefab, other, native);

        ControlNode Control(int i, int input, int output)
            => new() { Position = positions[i], Direction = -Vector3.UnitZ,
                InputLines = [input, -1], OutputLines = [output, -1] };
        static NavCurve Curve(Vector3 a, Vector3 b, int[] next, int[] previous)
        {
            var curve = new NavCurve
            {
                StartPosition = a, EndPosition = b, Length = Vector3.Distance(a, b),
                StartRotation = Quaternion.Identity, EndRotation = Quaternion.Identity, SemaphoreId = -1
            };
            foreach (int index in next) curve.NextLines.Add(index);
            foreach (int index in previous) curve.PreviousLines.Add(index);
            return curve;
        }
    }

    [Fact]
    public void LoadedPrefabWithoutNavigationCurvesExplainsTheConnectionFailure()
    {
        var fs=new Files();
        fs.Content["/def/world/prefab.test.sii"]="SiiNunit { prefab_model : prefab.test { prefab_desc: \"/prefab/port.ppd\" } }";
        fs.Content["/prefab/port.ppd"]="cached binary descriptor";
        var map=new TruckLib.ScsMap.Map();var node=new Node{Uid=10,Position=Vector3.Zero};
        var prefab=new Prefab{Uid=42,Model=new Token("test"),Nodes=[node]};node.ForwardItem=prefab;
        map.Nodes.Add(node.Uid,node);map.MapItems.Add(prefab.Uid,prefab);
        using var data=new GameDataManager(fs,map:map);
        data.Projection("ppd","/prefab/port.ppd",()=>new PrefabDescriptor{Nodes=[new ControlNode()],NavCurves=[]});
        var failure=Assert.IsType<MapQueryFailure>(data.DescribeMissingConnection(map,node.Uid));
        Assert.Equal(MapQueryFailureKind.DefinitionUnavailable,failure.Kind);
        Assert.Contains("000000000000002A",failure.Reason);Assert.Contains("/prefab/port.ppd",failure.Reason);
        Assert.Contains("NavCurves=0",failure.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SdkPassageThroughAnOrdinaryPrefabWithNoNativePathThrows(bool withRetryScheduler)
    {
        var (data,window,route)=MissingPrefabPassage();
        using var dataset=data;
        if(withRetryScheduler)
        {
            var scheduler=new MapRetryCoordinator();
            scheduler.Adopt(window,1);
            window=window with{Retries=scheduler};
        }
        using var paths=new RoadPathSource(window);
        for(int attempt=0;attempt<2;attempt++)
        {
            var exception=Assert.Throws<InvalidDataException>(()=>paths.GetRouteComponents(route));
            Assert.Contains("000000000000002A (/prefab/facility.ppd)",exception.Message);
            Assert.Contains("000000000000000A → 000000000000000B",exception.Message);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitFacilityMayHaveNoNativePath(bool ferry)
    {
        var (data,window,route)=MissingPrefabPassage(ferry:ferry,company:!ferry);
        using var dataset=data;
        using var paths=new RoadPathSource(window);
        Assert.Empty(paths.GetRouteComponents(route));
    }

    [Fact]
    public void KnownCompanyInANeighboringSectorDoesNotInvalidateItsPrefabPassage()
    {
        var (data,host,route)=MissingPrefabPassage(neighboringCompany:true);
        using var dataset=data;
        var prefab=data.GetPrefab(42)!.Instance;
        var company=Assert.Single(prefab.SlaveItems.OfType<Company>());
        Assert.Same(prefab,company.Prefab);
        Assert.Same(company,data.Map.GetItem<Company>(company.Uid));

        var window=new TruckLib.ScsMap.Map();
        SectorCoordinate sector=new(0,0);
        new HostMapView(host.Map).MergeInto(window,[sector]);
        Assert.False(window.MapItems.ContainsKey(company.Uid));
        var companies=data.Companies.Build(window);
        Assert.Empty(companies.Places);
        var snapshot=new NearbyMapSnapshot(window,[],[],[sector]){Data=data,Companies=companies,
            MissingConnection=uid=>data.DescribeMissingConnection(window,uid)};
        using var paths=new RoadPathSource(snapshot);
        Assert.Empty(paths.GetRouteComponents(route));
        Assert.DoesNotContain(paths.MapIssues,issue=>issue is MissingMapObjects);
    }

    [Fact]
    public void AProjectedOutPathIsNotAMissingNativePath()
    {
        var (data,window,route)=MissingPrefabPassage(nativePath:true);
        using var dataset=data;
        var prefab=data.GetPrefab(42)!;
        var description=prefab.Description!;
        Assert.True(PrefabLaneTopology.Reaches(prefab.Instance,description,10,11));
        Assert.NotEmpty(PrefabPathGeometry.Build(prefab.Instance,description));
        var access=PrefabPlayerAccess.Build(prefab.Description!,[false,null],[null,false]);
        Assert.Empty(PrefabPassageLengths.Read(prefab.Instance,access.Descriptor,10,11));
        window=window with{PrefabPaths=PrefabPathGeometry.Build(prefab.Instance,access.Descriptor)};
        using var paths=new RoadPathSource(window);
        Assert.Empty(paths.GetRouteComponents(route));
        Assert.Contains(paths.MapIssues,i=>i is MapQueryFailure{Kind:MapQueryFailureKind.DefinitionUnavailable});
    }

    [Fact]
    public void AnUnloadedPrefabInterfaceRemainsAMapDependency()
    {
        var (data,window,route)=MissingPrefabPassage();
        using var dataset=data;
        window.Map.Nodes.Remove(11);
        using var paths=new RoadPathSource(window);
        Assert.Empty(paths.GetRouteComponents(route));
        Assert.Contains(paths.MapIssues.OfType<MissingMapObjects>(),i=>i.Keys.Contains(new(MapObjectKind.Node,11)));
    }

    private static (GameDataManager Data,NearbyMapSnapshot Window,RouteSnapshot Route) MissingPrefabPassage(
        bool ferry=false,bool company=false,bool nativePath=false,bool neighboringCompany=false)
    {
        var fs=new Files();
        fs.Content["/def/world/prefab.test.sii"]="SiiNunit { prefab_model : prefab.test { prefab_desc: \"/prefab/facility.ppd\" } }";
        fs.Content["/prefab/facility.ppd"]="cached binary descriptor";
        var map=new TruckLib.ScsMap.Map();
        var entry=new Node{Uid=10,Position=Vector3.Zero};
        var exit=new Node{Uid=11,Position=new(0,0,20)};
        var prefab=new Prefab{Uid=42,Model=new Token("test"),Nodes=[entry,exit]};
        entry.ForwardItem=prefab;exit.BackwardItem=prefab;
        map.Nodes.Add(entry.Uid,entry);map.Nodes.Add(exit.Uid,exit);map.MapItems.Add(prefab.Uid,prefab);
        if(ferry)prefab.Ferry=Ferry.Add(map,new(0,0,10),"terminal");
        if(neighboringCompany)Company.Add(map,prefab,new(4100,0,0));
        var data=new GameDataManager(fs,map:map);
        data.Prepare();
        data.Projection("ppd","/prefab/facility.ppd",()=>new PrefabDescriptor
        {
            Nodes=[new ControlNode{Position=entry.Position,InputLines=nativePath?[0]:[],OutputLines=[]},
                new ControlNode{Position=exit.Position,InputLines=[],OutputLines=nativePath?[0]:[]}],
            NavCurves=nativePath?[new NavCurve{StartPosition=entry.Position,EndPosition=exit.Position,Length=20}]:[]
        });
        var window=new NearbyMapSnapshot(map,[],[],[]){Data=data,MissingConnection=uid=>data.DescribeMissingConnection(map,uid),
            Companies=new(company?[new(90,prefab.Uid,"terminal","",null)]:[],[prefab])};
        return (data,window,RouteSnapshot.Parse([new(entry.Uid,20,1),new(exit.Uid,0,0)],DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task SharedSiiAndIncludesAreReadAndParsedOnceAcrossConcurrentQueries()
    {
        var fs=new Files();
        fs.Content["/def/world/models.sii"]="SiiNunit\n{\n@include \"shared.sui\"\n}\n";
        fs.Content["/def/world/shared.sui"]="model_def : model.a\n{\nmodel_desc: \"/model/a.pmd\"\n}\n";
        var data=new GameDataManager(fs);
        var documents=await Task.WhenAll(Enumerable.Range(0,12).Select(_=>Task.Run(()=>data.GetSii("/def/world/./models.sii"))));
        Assert.All(documents,d=>Assert.Same(documents[0],d));
        Assert.Same(documents[0],data.GetSii("def\\world\\models.sii"));
        Assert.Equal("model.a",Assert.Single(documents[0].OfClass("model_def")).Name);
        Assert.Single(documents[0].Named("model.a"));
        Assert.Equal(1,fs.Reads["/def/world/models.sii"]);
        Assert.Equal(1,fs.Reads["/def/world/shared.sui"]);
        Assert.Equal(1,data.Statistics.SiiParses);
    }

    [Fact]
    public void DirectoryAndFailedResourceLookupsAreNotRepeated()
    {
        var fs=new Files();var data=new GameDataManager(fs);
        data.GetFiles("/def/world/");data.GetFiles("def/world");
        Assert.Equal(1,fs.Scans["/def/world"]);
        Assert.Throws<FileNotFoundException>(()=>data.GetSii("/missing.sii"));
        Assert.Throws<FileNotFoundException>(()=>data.GetSii("missing.sii"));
        Assert.Equal(1,fs.Reads["/missing.sii"]);
    }

    [Fact]
    public void MapQueriesCombineNativeObjectsAndAdditionalSectorData()
    {
        var native=new TruckLib.ScsMap.Map();var road=Road.Add(native,new(3990,0,0),new(4010,0,0),default);
        var data=new GameDataManager(new Files(),map:native);
        var extra=new TruckLib.ScsMap.Map();var sign=Sign.Add(extra,new(4100,0,0),default);
        data.Map.Publish(extra);
        Assert.Same(road,data.Map.GetItem<Road>(road.Uid));
        Assert.Same(sign,data.Map.GetItem<Sign>(sign.Uid));
        Assert.Same(road.Node,data.Map.GetNode(road.Node.Uid));
        Assert.Same(sign.Node,data.Map.GetNode(sign.Node.Uid));
        Assert.Equal(2,data.Map.GetSector(new(1,0)).Items.Count);
        Assert.Same(sign,Assert.Single(data.Map.GetItems<Sign>()));
        Assert.Single(native.MapItems);Assert.Single(extra.MapItems);
    }

    [Fact]
    public void StandaloneReadersCanShareTheSameDatasetScope()
    {
        var source=new Files();
        Assert.Same(GameDataManager.From(source),GameDataManager.From(source));
    }

    [Fact]
    public void PreparationSharesCatalogueWithWorldAndFeatureReaders()
    {
        var fs=new Files();
        fs.Content["/def/world/sign.sii"]="SiiNunit { sign_model : sign.test { model_desc: \"/model/test.pmd\" traffic_rule: \"\" } }";
        fs.Content["/def/company.sii"]="SiiNunit { company_permanent : company.permanent.test { name: \"Test Depot\" } }";
        fs.Content["/def/sign/templates.sii"]="SiiNunit { sign_template_def : template.test { path: \"/sign/test.sii\" } }";
        fs.Content["/def/sign/boards.sii"]="SiiNunit { sign_board_def : sign_board.test { path: \"/sign/board.sii\" } }";
        fs.Content["/sign/test.sii"]="SiiNunit { sign_template_board_binding : .binding { area_name: \"main\" linked_board: \"sign_board.test\" } }";
        fs.Content["/sign/board.sii"]="SiiNunit { sign_template_text : .text { id: 1 text: \"Test City\" } }";
        var data=new GameDataManager(fs);
        var definition=data.World.Signs["test"];
        data.Prepare(TestContext.Current.CancellationToken);
        long preparedParses=data.Statistics.SiiParses;
        Assert.Equal("Test Depot",data.Companies.ReadNames()["test"]);
        var native=new TruckLib.ScsMap.Map();var sign=Sign.Add(native,new(0,0,0),default);
        sign.SignTemplate="template.test";
        Assert.Equal(["Test City"],new SignTextReader(data).ReadTexts(sign));
        Assert.Equal(["Test City"],new SignTextReader(data).ReadTexts(sign));
        Assert.Equal(preparedParses+2,data.Statistics.SiiParses);
        Assert.Same(definition.Unit,Assert.Single(data.GetSii(definition.Resource).Named("sign.test")));
        Assert.All(fs.Reads,p=>Assert.Equal(1,p.Value));
        data.Prepare(TestContext.Current.CancellationToken);
        Assert.Equal(preparedParses+2,data.Statistics.SiiParses);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LaterSectorPublicationPreservesExistingSnapshotsAndNativeOwnership(bool keepHistory)
    {
        var native=new TruckLib.ScsMap.Map();var road=Road.Add(native,new(0,0,0),new(20,0,0),default);
        var data=new GameMapData(native,keepHistory);
        var first=new TruckLib.ScsMap.Map();var a=Sign.Add(first,new(4100,0,0),default);
        data.Publish(first);
        var old=data.GetItems<Sign>();
        var second=new TruckLib.ScsMap.Map();var b=Sign.Add(second,new(8100,0,0),default);
        data.Publish(second);
        Assert.Same(keepHistory?a:null,data.GetItem<Sign>(a.Uid));
        Assert.Same(keepHistory?a.Node:null,data.GetNode(a.Node.Uid));
        Assert.Same(b,data.GetItem<Sign>(b.Uid));
        Assert.Same(road,data.GetItem<Road>(road.Uid));
        Assert.Same(a,Assert.Single(old));
        Assert.Equal(keepHistory?2:1,data.GetItems<Sign>().Count);
        Assert.Equal(keepHistory?1:0,data.GetSector(new(1,0)).Items.Count);
        Assert.Single(data.GetSector(new(2,0)).Items);
        Assert.Single(native.MapItems);
    }

    [Fact]
    public void BrokenCataloguePropagatesItsCachedOriginalParsingFailure()
    {
        var fs=new Files();fs.Content["/def/sign/templates.sii"]="not a SII document";
        using var data=new GameDataManager(fs);
        var original=Record.Exception(()=>data.GetSii("/def/sign/templates.sii"));Assert.NotNull(original);
        Assert.Same(original,Record.Exception(()=>data.Prepare(TestContext.Current.CancellationToken)));
        Assert.Equal(1,fs.Reads["/def/sign/templates.sii"]);
        Assert.Empty(data.Diagnostics);
    }

    [Fact]
    public void CatalogueProjectionErrorsAreNotConvertedIntoMissingDefinitions()
    {
        var fs=new Files();fs.Content["/def/test.sii"]="SiiNunit { test : .one {} }";
        using var data=new GameDataManager(fs);var original=new NotSupportedException("unhandled projection");
        Assert.Same(original,Assert.Throws<NotSupportedException>(()=>data.TryDefinitions("/def/test.sii",_=>throw original)));
        Assert.Contains(nameof(CatalogueProjectionErrorsAreNotConvertedIntoMissingDefinitions),original.StackTrace);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingConnectionSeparatesAbsentDescriptorFromReadFailure(bool absent)
    {
        var fs=new Files();fs.Content["/def/world/prefab.test.sii"]="SiiNunit { prefab_model : prefab.test { prefab_desc: \"/prefab/test.ppd\" } }";
        if(!absent)fs.Content["/prefab/test.ppd"]="present descriptor";
        var original=new IOException("asset read failed");fs.Failures["/prefab/test.ppd"]=original;
        var map=new TruckLib.ScsMap.Map();var node=new Node{Uid=10,Position=Vector3.Zero};
        var prefab=new Prefab{Uid=42,Model=new Token("test"),Nodes=[node]};node.ForwardItem=prefab;
        map.Nodes.Add(10,node);map.MapItems.Add(42,prefab);
        using var data=new GameDataManager(fs,map:map);
        MapQueryIssue Query()=>data.DescribeMissingConnection(map,10);
        if(absent)
        {
            Assert.Contains("/prefab/test.ppd is missing",Assert.IsType<MapQueryFailure>(Query()).Reason);
            Assert.False(fs.Reads.ContainsKey("/prefab/test.ppd"));
        }
        else
        {
            Assert.Same(original,Assert.Throws<IOException>(()=>Query()));
            Assert.Contains(nameof(Files.ReadAllBytes),original.StackTrace);
        }
    }

    [Fact]
    public void CancelledPreparationCanBeRetried()
    {
        var data=new GameDataManager(new Files());
        using var stop=new CancellationTokenSource();stop.Cancel();
        Assert.Throws<OperationCanceledException>(()=>data.Prepare(stop.Token));
        Assert.Equal(0,data.Statistics.ResourceReads);
        data.Prepare(TestContext.Current.CancellationToken);Assert.NotNull(data.SpeedLimits);
    }

    private sealed class Files:IFileSystem
    {
        public readonly Dictionary<string,string> Content=new(StringComparer.Ordinal);
        public readonly Dictionary<string,byte[]> BinaryContent=new(StringComparer.Ordinal);
        public readonly Dictionary<string,Exception> Failures=new(StringComparer.Ordinal);
        public readonly ConcurrentDictionary<string,int> Reads=new(StringComparer.Ordinal),Scans=new(StringComparer.Ordinal);
        public char DirectorySeparator=>'/';
        public bool FileExists(string path)=>Content.ContainsKey(path)||BinaryContent.ContainsKey(path);
        public bool DirectoryExists(string path)=>Content.Keys.Concat(BinaryContent.Keys).Any(p=>p.StartsWith(path.TrimEnd('/')+"/",StringComparison.Ordinal));
        public IList<string> GetFiles(string path)
        {Scans.AddOrUpdate(path,1,(_,n)=>n+1);return Content.Keys.Concat(BinaryContent.Keys).Where(p=>GetParent(p)==path.TrimEnd('/')).ToArray();}
        public byte[] ReadAllBytes(string path)
        {Reads.AddOrUpdate(path,1,(_,n)=>n+1);if(Failures.TryGetValue(path,out var failure))throw failure;return BinaryContent.TryGetValue(path,out var bytes)?bytes.ToArray():Content.TryGetValue(path,out var text)?Encoding.UTF8.GetBytes(text):throw new FileNotFoundException(path);}
        public Stream Open(string path)=>new MemoryStream(ReadAllBytes(path),false);
        public string ReadAllText(string path)=>ReadAllText(path,Encoding.UTF8);
        public string ReadAllText(string path,Encoding encoding)=>encoding.GetString(ReadAllBytes(path));
        public string GetParent(string path)=>path[..path.LastIndexOf('/')];
    }
}
