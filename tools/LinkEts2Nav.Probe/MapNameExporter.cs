using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Diagnostics;
using LinkEts2Nav.Map;
using TruckLib;
using TruckLib.ScsMap;
using TruckLib.Sii;
namespace LinkEts2Nav.Probe;

/// <summary>Offline inventory only. Unclassified board text is retained for review, never guessed to be a city.</summary>
internal static class MapNameExporter
{
    private sealed record Name(string Text,string Source,string Resource,string? ItemUid=null,string? Detail=null);
    private sealed record LegacyLabel(string Token,string Kind,string Resource,string ItemUid);
    public static void Export(IFileSystem fs,string output)
    {
        Directory.CreateDirectory(output);
        var names=new List<Name>();var candidates=new List<Name>();var diagnostics=new List<object>();var legacyLabels=new List<LegacyLabel>();
        var companyReader=new CompanyPlaceReader(fs);
        var companyNames=companyReader.ReadNames().Where(p=>p.Value is not null)
            .Select(p=>new Name(p.Value!,"company-definition","company.permanent."+p.Key)).ToList();
        diagnostics.AddRange(companyReader.Diagnostics.Select(d=>(object)new{reason="CompanyNameUnresolved",detail=d}));
        var cityTokens=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var path in fs.GetFiles("/def/").Where(p=>fs.GetParent(p).TrimEnd('/')=="/def"&&Path.GetFileName(p).StartsWith("city",StringComparison.Ordinal)&&p.EndsWith(".sii",StringComparison.Ordinal)).Order())
        {
            try { foreach(var unit in SiiFile.Open(path,fs).Units.Where(u=>u.Class=="city_data"))
                foreach(var key in new[]{"city_name","city_name_localized","short_city_name"})
                {
                    string text=Text(unit,key);
                    if(text.StartsWith("@@",StringComparison.Ordinal))
                    {diagnostics.Add(new{resource=path,reason="NameRequiresLocalization",unit=unit.Name,attribute=key,raw=text});continue;}
                    if(!Valid(text))continue;
                    names.Add(new(text,"city-definition",path,Detail:unit.Name+":"+key));
                    if(key=="city_name")cityTokens[unit.Name.Split('.').Last()]=text;
                }
            }catch(Exception e){diagnostics.Add(new{resource=path,reason=e.GetType().Name,message=e.Message});}
        }
        var reader=new SignTextReader(fs);var rules=new SpeedLimitDefinitionReader(fs);var rawLabels=new List<Name>();int signs=0,legacyRoads=0,unreadable=0;var seen=new HashSet<ulong>();
        var sectors=fs.GetFiles("/map/europe/").Where(p=>p.EndsWith(".base",StringComparison.Ordinal)).Order().ToArray();
        foreach(var file in sectors)
        {
            try
            {
                var map=new SignMap();
                using var trace=new StringWriter(CultureInfo.InvariantCulture);
                using var listener=new TextWriterTraceListener(trace);
                Trace.Listeners.Add(listener);
                var console=Console.Out;using var readOutput=new StringWriter(CultureInfo.InvariantCulture);
                try { Console.SetOut(readOutput);map.Read("/map/europe.mbd",fs,[Sector.SectorCoordsFromSectorFilePath(file)]); }
                finally { Console.SetOut(console);Trace.Listeners.Remove(listener);listener.Flush(); }
                if(!readOutput.ToString().Contains("with 0 failures.",StringComparison.Ordinal))
                    diagnostics.Add(new{resource=file,reason="SectorReadFailure",summary=readOutput.ToString(),detail=trace.ToString()});
                var ruleSigns=rules.Build(map).Signs.ToDictionary(s=>s.Id,StringComparer.Ordinal);
                foreach(var sign in LoadedMapItems.Enumerate(map.MapItems.Values).OfType<Sign>().Where(s=>seen.Add(s.Uid)))
                {
                    signs++;string uid=sign.Uid.ToString("X16");var texts=reader.ReadTexts(sign);
                    foreach(var raw in reader.ReadRawTexts(sign))rawLabels.Add(new(raw,"raw-sign-text",file,uid,sign.SignTemplate));
                    diagnostics.AddRange(reader.Diagnostics.Select(d=>(object)new{resource=file,itemUid=uid,reason=d.Reason,source=d.Resource,detail=d.Detail}));
                    bool city=ruleSigns.TryGetValue(uid,out var rule)&&rule.Action.Urban is not null&&!rule.Action.Unsupported&&!rule.TemplateOverride;
                    foreach(var text in texts) (city?names:candidates).Add(new(text,city?"town-sign":"sign-text-candidate",file,uid,sign.SignTemplate));
                    foreach(var board in sign.SignBoards??[])
                    {
                        foreach(var token in new[]{board.City1,board.City2}.Select(t=>t.ToString()).Where(t=>t.Length>0))
                        {
                            legacyLabels.Add(new(token,"city",file,uid));
                            if(cityTokens.TryGetValue(token,out var name))names.Add(new(name,"legacy-sign-city",file,uid,token));
                            else diagnostics.Add(new{resource=file,itemUid=uid,reason="UnknownLegacyCityToken",token});
                        }
                        if(board.Road.ToString().Length>0){legacyRoads++;legacyLabels.Add(new(board.Road.ToString(),"road-label",file,uid));diagnostics.Add(new{resource=file,itemUid=uid,reason="LegacyRoadTokenNotRoadUid",token=board.Road.ToString()});}
                    }
                    if(texts.Count==0){unreadable++;diagnostics.Add(new{resource=file,itemUid=uid,reason="NoReadableTemplateText",model=sign.Model.ToString(),template=sign.SignTemplate});}
                }
            }catch(Exception e){diagnostics.Add(new{resource=file,reason=e.GetType().Name,message=e.Message});}
            Console.WriteLine($"Place-name scan {Array.IndexOf(sectors,file)+1}/{sectors.Length}: {signs} signs, {names.Count} name sources, {candidates.Count} candidate sources, {diagnostics.Count} diagnostics");
        }
        var known=names.Select(n=>n.Text).ToHashSet(StringComparer.Ordinal);
        // A direction-board exact spelling already present in the name catalog is confirmed; retain every source.
        names.AddRange(candidates.Where(n=>known.Contains(n.Text)).Select(n=>n with{Source="direction-sign-known-name"}));
        candidates=candidates.Where(n=>!known.Contains(n.Text)).ToList();
        Write(output,"names",names);Write(output,"candidates",candidates);Write(output,"company-names",companyNames);
        File.WriteAllText(Path.Combine(output,"legacy-sign-labels.json"),JsonSerializer.Serialize(legacyLabels,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output,"inventory.json"),JsonSerializer.Serialize(new{Schema=1,sectors=sectors.Length,signs,unreadable,legacyRoads,Names=names,CompanyNames=companyNames,Candidates=candidates,RawSignTexts=rawLabels,Diagnostics=diagnostics,
            Limitations=new[]{"Free board text may denote towns, roads, facilities, distances or other labels; candidates need review.","Model texture lettering without text objects cannot be recovered by this parser.","Unconfirmed visibility overrides are not interpreted; candidate text may be hidden."}},new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));
        Console.WriteLine($"Place names exported: {names.Select(n=>n.Text).Distinct().Count()} names; {candidates.Select(n=>n.Text).Distinct().Count()} candidate texts; {diagnostics.Count} diagnostics.");
    }
    public static void ExportCompanies(IFileSystem fs,string output)
    {
        Directory.CreateDirectory(output);var reader=new CompanyPlaceReader(fs);
        var names=reader.ReadNames().Where(p=>p.Value is not null).Select(p=>new Name(p.Value!,"company-definition","company.permanent."+p.Key)).ToList();
        Write(output,"company-names",names);
        File.WriteAllText(Path.Combine(output,"company-inventory.json"),JsonSerializer.Serialize(new{Names=names,Diagnostics=reader.Diagnostics},new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));
        Console.WriteLine($"Company names exported: {names.Count} definitions, {reader.Diagnostics.Count} diagnostics.");
    }
    private static void Write(string output,string stem,List<Name> entries)
        {
            string root=Path.GetFullPath(output);
            // Delete only this exporter's content-addressed job-list batches, never user audio or unrelated files.
            foreach(string stale in Directory.EnumerateFiles(root,stem+".*.jobs.json"))
                if(System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(stale),"^"+System.Text.RegularExpressions.Regex.Escape(stem)+@"\.\d+\.jobs\.json$"))
                    File.Delete(stale);
            var text=entries.Select(n=>n.Text).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            File.WriteAllLines(Path.Combine(output,stem+".txt"),text,new UTF8Encoding(false));
            foreach(var chunk in text.Chunk(2000).Select((items,index)=>(items,index)))
                File.WriteAllText(Path.Combine(output,stem+"."+chunk.index.ToString("D3")+".jobs.json"),JsonSerializer.Serialize(new{Schema=1,Kind="city-names",Items=chunk.items.Select(t=>new{Text=t,File=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(t)))+".wav"})},new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));
        }
    private static bool Valid(string t)=>t.Length is >0 and <=128&&t.Any(char.IsLetter)&&!t.Any(char.IsControl);
    private static string Text(Unit unit,string key)
    {
        if(!unit.Attributes.TryGetValue(key,out var value)||value is null)return "";
        object v=value;return (Convert.ToString(v is Token token?token.ToString():v.GetType().GetProperty("Value")?.GetValue(v)??v,CultureInfo.InvariantCulture)??"").Trim('"').Trim().Normalize();
    }
    private sealed class SignMap:TruckLib.ScsMap.Map
    {
        protected override bool PostProcessItem(MapItem item)=>item is Sign ||item is Compound c&&LoadedMapItems.RetainSupplementSigns(c);
    }
}
