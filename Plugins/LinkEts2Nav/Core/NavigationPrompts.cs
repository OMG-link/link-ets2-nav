using System.Globalization;

namespace LinkEts2Nav.Core;

public static class NavigationPrompts
{
    public static string Action(ManeuverKind kind) => kind switch
    {
        ManeuverKind.TurnLeft => "左转", ManeuverKind.TurnRight => "右转",
        ManeuverKind.KeepLeft => "向左前方行驶", ManeuverKind.KeepRight => "向右前方行驶",
        ManeuverKind.MergeLeft => "向左前方进入主路", ManeuverKind.MergeRight => "向右前方进入主路",
        ManeuverKind.EnterRoundabout => "进入环岛", ManeuverKind.ExitRoundabout => "驶出环岛", ManeuverKind.PassRoundaboutExit => "经过出口",
        ManeuverKind.Straight => "直行", ManeuverKind.UTurn => "掉头",
        ManeuverKind.ChooseLanes => "按指定车道行驶",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string Number(int value)
    {
        if (value is < 0 or > 9999) throw new ArgumentOutOfRangeException(nameof(value));
        if (value == 0) return "零";
        const string digits = "零一二三四五六七八九";
        string result = "";
        bool zero = false;
        foreach (var (divisor, unit) in new[] { (1000,"千"), (100,"百"), (10,"十"), (1,"") })
        {
            int digit = value / divisor;
            value %= divisor;
            if (digit == 0) { if (result.Length > 0 && value > 0) zero = true; continue; }
            if (zero) { result += "零"; zero = false; }
            if (!(divisor == 10 && digit == 1 && result.Length == 0)) result += digits[digit];
            result += unit;
        }
        return result;
    }

    public static string AtDistance(ManeuverKind kind, int meters) => AtDistance(kind,meters,0);

    public static string AtDistance(ManeuverKind kind, int meters, int ordinal, string? instruction = null)
    {
        if (meters is < 50 or > 2000 || meters % 50 != 0)
            throw new ArgumentOutOfRangeException(nameof(meters));
        return Distance(meters, round: false) + "后，" + (meters<=200 && ordinal>1 ? "在第"+Number(ordinal)+"个路口" : "") + (instruction ?? Action(kind)) + "。";
    }

    public static string Execute(ManeuverKind kind, string? instruction = null) => (instruction ?? Action(kind)) + "。";

    /// <summary>Announces a second nearby action relative to the action being executed.</summary>
    public static string Then(string first, ManeuverKind next, int meters, string? instruction = null)
    {
        if (meters < 0) throw new ArgumentOutOfRangeException(nameof(meters));
        return first.TrimEnd('。') + "，然后" + (meters > 0 ? Number(meters) + "米" : "立即")
            + (instruction ?? Action(next)) + "。";
    }

    public static string Immediate(ManeuverKind kind, string? instruction = null)
        => kind == ManeuverKind.Straight ? "保持直行。" : "即将" + (instruction ?? Action(kind)) + "。";

    // Zero ordinal means topology could not prove how many physical intersections lie ahead.
    public static string FirstLayer(ManeuverKind kind, double distance, int junctionOrdinal, string? instruction = null)
    {
        if (!double.IsFinite(distance) || distance < 0) throw new ArgumentOutOfRangeException(nameof(distance));
        if (distance > 800) return Continue(distance);
        int meters = (int)Math.Floor(distance / 50) * 50;
        if (meters < 50 && junctionOrdinal <= 1) return Immediate(kind,instruction);
        string where = distance>200 || junctionOrdinal <= 1 ? "" : "在第" + Number(junctionOrdinal) + "个路口";
        return (meters >= 50 ? MeterNumber(meters) + "米后，" : "") + where + (instruction ?? Action(kind)) + "。";
    }

    public static string Continue(double distance)
        => "沿当前道路，继续行驶" + Distance(distance) + "。";
    public static string Continue() => "在听到下一条语音提示前，请沿当前道路行驶。";

    public static int DistanceStep(double distance) => distance > 10000 ? 1000 : distance > 1000 ? 100 : 50;

    public static double RoundDistance(double distance)
    {
        if (!double.IsFinite(distance) || distance < 0) throw new ArgumentOutOfRangeException(nameof(distance));
        int step = DistanceStep(distance);
        return Math.Floor(distance / step) * step;
    }

    private static string MeterNumber(int value) => Number(value).Replace("二百", "两百", StringComparison.Ordinal);

    private static string Distance(double distance, bool round = true)
    {
        if (!double.IsFinite(distance) || distance < 50) throw new ArgumentOutOfRangeException(nameof(distance));
        double meters = round ? RoundDistance(distance) : distance;
        if (meters < 1000) return MeterNumber((int)meters) + "米";
        double kilometers = meters / 1000;
        int whole = (int)Math.Min(int.MaxValue, Math.Floor(kilometers));
        string number = whole <= 9999 ? Number(whole) : whole.ToString(CultureInfo.InvariantCulture);
        int hundredths = (int)Math.Round((kilometers - Math.Floor(kilometers)) * 100);
        if (hundredths != 0)
        {
            const string digits = "零一二三四五六七八九";
            string fraction = hundredths.ToString("00", CultureInfo.InvariantCulture).TrimEnd('0');
            number += "点" + string.Concat(fraction.Select(c => digits[c-'0']));
        }
        if (hundredths == 0) number = whole == 2 ? "两" : number.Replace("二百", "两百", StringComparison.Ordinal);
        return number + "公里";
    }

    public static string Advance(ManeuverKind kind) => AtDistance(kind,500);
    public static string Near(ManeuverKind kind) => AtDistance(kind,50);
    public static string Generic(ManeuverKind kind) => Immediate(kind);
    public static IEnumerable<string> AllTexts()
    {
        foreach (var kind in Enum.GetValues<ManeuverKind>())
        {
            // Lane selection carries its actual lane set in Instruction and is
            // synthesized through the normal dynamic-text cache.
            if(kind==ManeuverKind.ChooseLanes)continue;
            foreach (int distance in new[] { 2000,1000,500,450,400,350,300,250,200,150,100,50 })
                yield return AtDistance(kind,distance);
            yield return Immediate(kind);
            yield return Execute(kind);
        }
    }
}

