using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinkEts2Nav.Core;

namespace LinkEts2Nav.Speech;

/// <summary>Only explicitly generated pack entries can override the local voice.</summary>
public sealed class SpeechPack(string? directory, string cacheDirectory)
{
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static string FileName(string text) => SpeechPackFormat.FileName(text);
    public static readonly int[] CommonLimits = [30, 40, 50, 60, 70, 80, 90, 100];
    public const string UncalibratedText = "正靠近未校准的探头，无法获取其监测方向，请减速慢行。";
    public static IEnumerable<string> DefaultTexts() => SpeechTextCatalog.DefaultTexts;

    public static string NavigationDistance(int meters)
    {
        if (meters < 1000) return Number(meters).Replace("二百", "两百", StringComparison.Ordinal) + "米";
        string text = Number(meters / 1000);
        string fraction = (meters % 1000).ToString("000", System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0');
        if (fraction.Length > 0) text += "点" + string.Concat(fraction.Select(c => JourneySpeechText.Digits[c - '0']));
        if (fraction.Length == 0) text = meters / 1000 == 2 ? "两" : text.Replace("二百", "两百", StringComparison.Ordinal);
        return text + "公里";
    }

    public static string Number(int number)
    {
        if (number is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(number));
        return JourneySpeechText.BelowTenThousand(number);
    }

    public (string Path, bool CacheHit) Generate(string text, Func<string, (string Path, bool CacheHit)> local,
        bool preparedOnly = false)
    {
        if (preparedOnly) local = _ => throw new PreparedSpeechUnavailableException();
        PackManifest? pack = null;
        try
        {
            if (directory is not null && File.Exists(Path.Combine(directory, "manifest.json")))
            {
                pack = SpeechPackFormat.ReadManifest(Path.Combine(directory, "manifest.json"));
                var exact = pack.Entries.SingleOrDefault(e => e.Text == text);
                if (exact is not null)
                {
                    using var source = File.OpenRead(SpeechPackFormat.EntryPath(directory, exact));
                    SpeechPackFormat.Verify(source, exact.Sha256);
                    string target = Path.Combine(cacheDirectory, "qwen-" + exact.Sha256 + ".wav");
                    // Verify the original even on a cache hit. A removed or damaged pack
                    // must still take its normal recovery path.
                    if (File.Exists(target) && new FileInfo(target).Length == source.Length &&
                        string.Equals(SpeechPackFormat.HashFile(target), exact.Sha256, StringComparison.OrdinalIgnoreCase))
                        return (target, true);
                    // Preserve ancillary RIFF chunks too: the cache is keyed by the digest of the complete source file.
                    source.Position = 0;
                    PcmAudio.Copy(target, source);
                    return (target, false);
                }
            }
        }
        catch (Exception ex) when (SpeechPackFormat.IsReadFailure(ex))
        {
            Console.Error.WriteLine("Speech pack unavailable: " + ex.GetType().Name);
            pack = null;
        }
        var journey = JourneySpeechText.Parse(text);
        var brief = directory is not null && journey is not null ? JourneySpeechPack.Read(directory, journey) : null;
        if (brief?.Save(cacheDirectory) is { } recordedJourney) return recordedJourney;
        var navigation = directory is null ? null : NavigationSpeechPack.Open(directory, pack);
        (string Path, bool CacheHit)? RecordedNavigation() =>
            navigation?.Render(text) is { } recorded
                ? NavigationSpeechPack.Save(cacheDirectory, "navigation", recorded) : null;
        if (!preparedOnly && RecordedNavigation() is { } complete) return complete;
        if (!preparedOnly && brief is not null && journey is not null && (brief.Distance is null) != (brief.Time is null))
        {
            int endDistance = text.IndexOf('，', journey.Heading.Length) + 1;
            // Pass the original clause to the existing model cache, retaining printed large
            // numbers and punctuation. A model failure keeps its ordinary worker protocol.
            var hybrid = brief.Distance is null
                ? brief with { Distance = PcmAudio.Read(local(text[journey.Heading.Length..endDistance]).Path) }
                : brief with { Time = PcmAudio.Read(local(text[endDistance..]).Path) };
            if (hybrid.Save(cacheDirectory, hybrid: true) is { } result) return result;
        }
        var company=Regex.Match(text,@"^(?<prefix>(?:.*，然后)?.*?)到达(?<name>[^\r\n]+?)货场(?<suffix>附近，本次导航结束。|。)$",
            RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));
        if(company.Success)
        {
            string prefix=company.Groups["prefix"].Value+"到达",suffix="货场"+company.Groups["suffix"].Value;
            string fallback=company.Groups["prefix"].Value+(suffix=="货场。"?"到达目的地。":"到达目的地附近，导航结束。");
            return GenerateNamedParts(text,fallback,
                [(prefix,false),(company.Groups["name"].Value,true),(suffix,false)],"company-arrival-v1","company",local,pack,preparedOnly);
        }
        var ferry=Regex.Match(text,@"^(?<prefix>.*?)进入(?<from>[^，。\r\n]+)码头，乘坐前往(?<to>[^，。\r\n]+)的轮渡。$",
            RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));
        if(ferry.Success)
        {
            string prefix=ferry.Groups["prefix"].Value;
            return GenerateNamedParts(text,prefix+"进入码头，按导航方向乘坐轮渡。",
                [(prefix+"进入",false),(ferry.Groups["from"].Value,true),("码头，乘坐前往",false),
                    (ferry.Groups["to"].Value,true),("的轮渡。",false)],"ferry-entry-v1","ferry",local,pack,preparedOnly);
        }
        var city=Regex.Match(text,@"^(?<prefix>.*?进入)(?<name>[^，。\r\n]+)城区，请注意减速慢行。$",
            RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));
        if(city.Success)
        {
            string prefix=city.Groups["prefix"].Value, suffix="城区，请注意减速慢行。";
            return GenerateNamedParts(text,prefix+suffix,[(prefix,false),(city.Groups["name"].Value,true),(suffix,false)],
                "city-entry-v1","city",local,pack,preparedOnly);
        }
        if (preparedOnly) return RecordedNavigation() ?? throw new PreparedSpeechUnavailableException();
        if (pack is null) return local(text);
        try
        {
            // Capture spoken numbers without inventing a speed limit.
            var match = Regex.Match(text, @"^(?:(?<distance>[零一二两三四五六七八九十百0-9]+)(?<where>米处|米内)，有测速照相|(?<ahead>前方有测速照相))(?:，限速(?<speed>[零一二两三四五六七八九十百0-9]+))?。$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success) return local(text);
            bool hasSpeed = match.Groups["speed"].Success;
            string middleText = match.Groups["ahead"].Success ? "前方有测速照相" :
                match.Groups["where"].Value + "，有测速照相";
            middleText += hasSpeed ? "，限速" : "。";
            var middle = pack.Entries.SingleOrDefault(e => e.Text == middleText);
            if (middle is null) return local(text);
            // Validate the cloud fragment before spending time on local inference.
            string middlePath = SpeechPackFormat.EntryPath(directory!, middle);
            SpeechPackFormat.VerifyWave(middlePath, middle.Sha256);
            var identity = JsonSerializer.Serialize(new { schema = 2, text, pack.Model, pack.Voice, middle.Sha256 });
            var paths = new List<string>();
            if (match.Groups["distance"].Success) paths.Add(local(match.Groups["distance"].Value).Path);
            paths.Add(middlePath);
            if (hasSpeed) paths.Add(local(match.Groups["speed"].Value).Path);
            // Hash actual local samples too, so changing the local voice invalidates the assembled file.
            var samples = paths.Select(PcmAudio.Read).ToArray();
            string key = Hash(Encoding.UTF8.GetBytes(identity + string.Join("", samples.Select(Hash))));
            string result = Path.Combine(cacheDirectory, "hybrid-" + key + ".wav");
            byte[] joined = samples.SelectMany(x => x).ToArray();
            if (File.Exists(result))
            {
                try { if (PcmAudio.Read(result).AsSpan().SequenceEqual(joined)) return (result, true); }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { }
            }
            PcmAudio.Write(result, joined);
            return (result, false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Speech pack unavailable; using local synthesis: " + ex.GetType().Name);
            return local(text);
        }
    }

    // The historic CityNames directory stores reusable original-language place
    // clips. Validate every requested name before invoking the local voice.
    private (string Path,bool CacheHit) GenerateNamedParts(string text,string fallback,
        (string Text,bool IsName)[] parts,string identity,string filePrefix,
        Func<string,(string Path,bool CacheHit)> local,PackManifest? fixedPack,bool preparedOnly)
    {
        (string Path,bool CacheHit) Generic() => Generate(fallback, local, preparedOnly);
        try
        {
            string nameDirectory=Path.Combine(directory??"","CityNames"),manifest=Path.Combine(nameDirectory,"manifest.json");
            if(directory is null||!File.Exists(manifest))return Generic();
            var pack=SpeechPackFormat.ReadManifest(manifest,SpeechPackFormat.MaximumNameEntries,SpeechPackFormat.MaximumNameManifestBytes);
            var names=new Dictionary<string,byte[]>(StringComparer.Ordinal);
            bool namesInVoice=true;
            foreach(string name in parts.Where(p=>p.IsName).Select(p=>p.Text).Distinct(StringComparer.Ordinal))
            {
                var clip=pack.Entries.SingleOrDefault(e=>e.Text==name);
                if(clip is null)return Generic();
                namesInVoice &= NavigationSpeechPack.InVoice(clip,pack);
                names[name]=ReadVerified(clip,nameDirectory);
            }
            // Foreign names are exact CityNames recordings. Recorded-only composition requires
            // every Chinese carrier (including a preceding turn) to belong to that same voice.
            var book=namesInVoice?NavigationSpeechPack.Open(directory,fixedPack,pack):null;
            if(book is not null)
            {
                using var joinedNames=new MemoryStream();
                int i=0;
                for(;i<parts.Length;i++)
                {
                    var part=parts[i];
                    byte[]? pcm=part.IsName?names[part.Text]:book.Render(part.Text);
                    if(pcm is null||!NavigationSpeechPack.Append(joinedNames,pcm))break;
                }
                if(i==parts.Length)return NavigationSpeechPack.Save(cacheDirectory,filePrefix,joinedNames.ToArray());
            }
            if(preparedOnly)return Generic();
            var samples=parts.Select(p=>p.IsName?names[p.Text]:PcmAudio.Read(Generate(p.Text,local).Path)).ToArray();
            string key=Hash(Encoding.UTF8.GetBytes(identity+"|"+text+"|"+string.Join("|",samples.Select(Hash))));
            string result=Path.Combine(cacheDirectory,filePrefix+"-"+key+".wav");
            byte[] joined=samples.SelectMany(x=>x).ToArray();
            if(File.Exists(result))
            {
                try{if(PcmAudio.Read(result).AsSpan().SequenceEqual(joined))return(result,true);}
                catch(Exception e) when(e is IOException or InvalidDataException){}
            }
            PcmAudio.Write(result,joined);return(result,false);
        }
        catch(Exception e) when(SpeechPackFormat.IsReadFailure(e))
        {
            Console.Error.WriteLine("Place-name fragment unavailable; using generic audio: "+e.GetType().Name);
            return Generic();
        }
    }

    internal static byte[] ReadVerified(PackEntry entry, string directory) => SpeechPackFormat.ReadEntry(directory, entry);
}
