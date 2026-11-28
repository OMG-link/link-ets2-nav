using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LinkEts2Nav.Core;

namespace LinkEts2Nav.Speech;

public sealed record PackEntry(string Text, string File, string Sha256)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Composed { get; init; }
    // Absent inherits the manifest profile. Empty means an imported recording with unknown synthesis settings.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VoiceProfile { get; init; }
}
public sealed record PackManifest(int Schema, string Model, string Voice, PackEntry[] Entries)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiProfile { get; init; }
}

/// <summary>Only explicitly generated pack entries can override the local voice.</summary>
public sealed class SpeechPack(string? directory, string cacheDirectory)
{
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static string FileName(string text) => Hash(Encoding.UTF8.GetBytes(text)) + ".wav";
    public static readonly int[] CommonLimits = [30, 40, 50, 60, 70, 80, 90, 100];
    public const string UncalibratedText = "正靠近未校准的探头，无法获取其监测方向，请减速慢行。";
    public static IEnumerable<string> DefaultTexts() => SpeechTextCatalog.DefaultTexts;

    public static string NavigationDistance(int meters)
    {
        if (meters < 1000) return Number(meters).Replace("二百", "两百", StringComparison.Ordinal) + "米";
        const string digits = "零一二三四五六七八九";
        string text = Number(meters / 1000);
        string fraction = (meters % 1000).ToString("000", System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0');
        if (fraction.Length > 0) text += "点" + string.Concat(fraction.Select(c => digits[c - '0']));
        if (fraction.Length == 0) text = meters / 1000 == 2 ? "两" : text.Replace("二百", "两百", StringComparison.Ordinal);
        return text + "公里";
    }

    public static string Number(int number)
    {
        if (number is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(number));
        const string digits = "零一二三四五六七八九";
        string text = number >= 1000 ? digits[number / 1000] + "千" : "";
        int hundreds = number / 100 % 10;
        if (hundreds > 0) text += digits[hundreds] + "百";
        else if (number >= 1000 && number % 100 > 0) text += "零";
        int rest = number % 100;
        if (hundreds > 0 && rest is > 0 and < 10) text += "零";
        if (rest >= 10) text += (rest / 10 == 1 && number < 100 ? "" : digits[rest / 10].ToString()) + "十";
        if (rest % 10 != 0) text += digits[rest % 10];
        return text;
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
                using var manifest = File.OpenRead(Path.Combine(directory, "manifest.json"));
                pack = JsonSerializer.Deserialize<PackManifest>(manifest);
                if (pack is not { Schema: 1, Entries: not null } || pack.Entries.Any(e => e is null))
                    throw new InvalidDataException("Unsupported speech pack.");
                var exact = pack.Entries.SingleOrDefault(e => e.Text == text);
                if (exact is not null)
                {
                    string source = Resolve(exact);
                    string target = Path.Combine(cacheDirectory, "qwen-" + exact.Sha256 + ".wav");
                    if (File.Exists(target) && Hash(File.ReadAllBytes(target)) == exact.Sha256) return (target, true);
                    PcmAudio.Write(target, PcmAudio.Read(source));
                    return (target, false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Speech pack unavailable: " + ex.GetType().Name);
            pack = null;
        }
        if (directory is not null && JourneySpeechText.Parse(text) is { } journey &&
            JourneySpeechPack.TryGenerate(directory, cacheDirectory, journey) is { } brief) return brief;
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
        if (directory is not null && NavigationSpeechPack.Open(directory, pack)?.Render(text) is { } recorded)
            return NavigationSpeechPack.Save(cacheDirectory, "navigation", recorded);
        if (preparedOnly) throw new PreparedSpeechUnavailableException();
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
            string middlePath = Resolve(middle);
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
            if(new FileInfo(manifest).Length>2097152)throw new InvalidDataException("Place-name manifest too large.");
            var pack=JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(manifest));
            if(pack is not {Schema:1,Entries:not null}||pack.Entries.Length>4096||pack.Entries.Any(e=>e is null))
                throw new InvalidDataException("Invalid place-name manifest.");
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
            if(File.Exists(Path.Combine(directory,NavigationSpeechPack.DirectoryName,"manifest.json")))
            {
                try{return Generate(fallback,local,preparedOnly:true);}
                catch(PreparedSpeechUnavailableException){ }
            }
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
        catch(Exception e) when(e is IOException or JsonException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Place-name fragment unavailable; using generic audio: "+e.GetType().Name);
            return Generic();
        }
    }

    private string Resolve(PackEntry entry,string? packDirectory=null)
    {
        string root = packDirectory ?? directory!;
        ReadVerified(entry, root);
        return Path.Combine(root, entry.File);
    }

    internal static byte[] ReadVerified(PackEntry entry, string directory)
    {
        if (entry.File != FileName(entry.Text)) throw new InvalidDataException("Unexpected pack filename.");
        using var file = File.OpenRead(Path.Combine(directory, entry.File));
        if (file.Length is < 44 or > PcmAudio.MaximumWaveBytes || Convert.ToHexString(SHA256.HashData(file)) != entry.Sha256)
            throw new InvalidDataException("Speech pack checksum mismatch.");
        file.Position = 0;
        return PcmAudio.Read(file);
    }
}
