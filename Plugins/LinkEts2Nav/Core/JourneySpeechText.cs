using System.Globalization;
using System.Text;

namespace LinkEts2Nav.Core;

// The journey formatter and the speech worker share these words and number spellings.
internal sealed record JourneySpeechText(string Heading, string DistanceNumber, bool Kilometers, string Minutes)
{
    internal const string Start = "准备出发，", Replan = "重新规划路线，";
    internal const string DistancePrefix = "全程", Meter = "米，", Kilometer = "公里，";
    internal const string TimePrefix = "大约需要", TimeSuffix = "分钟。";
    internal const string Digits = "零一二三四五六七八九", NumberSounds = Digits + "两十百千万亿点";
    internal string DistanceUnit => Kilometers ? Kilometer : Meter;
    internal string DistanceClause => DistancePrefix + DistanceNumber + DistanceUnit;
    internal string TimeClause => TimePrefix + Minutes + TimeSuffix;

    internal static string Format(bool start, string distanceWithUnit, string minutes) =>
        (start ? Start : Replan) + DistancePrefix + distanceWithUnit + "，" + TimePrefix + minutes + TimeSuffix;

    internal static JourneySpeechText? Parse(string text)
    {
        string? heading = text.StartsWith(Start, StringComparison.Ordinal) ? Start :
            text.StartsWith(Replan, StringComparison.Ordinal) ? Replan : null;
        if (heading is null || !text.AsSpan(heading.Length).StartsWith(DistancePrefix, StringComparison.Ordinal) ||
            !text.EndsWith(TimeSuffix, StringComparison.Ordinal)) return null;
        int numbers = heading.Length + DistancePrefix.Length;
        int bridge = text.IndexOf(TimePrefix, numbers, StringComparison.Ordinal);
        if (bridge < 0) return null;
        string distance = text[numbers..bridge];
        bool km = distance.EndsWith(Kilometer, StringComparison.Ordinal);
        if (!km && !distance.EndsWith(Meter, StringComparison.Ordinal)) return null;
        string number = distance[..^(km ? Kilometer.Length : Meter.Length)];
        int point = number.IndexOf('点');
        string integer = point < 0 ? number : number[..point];
        if (!TryNumber(integer, out int whole) || (km ? whole < 1 : whole > 1000 || whole % 10 != 0)) return null;
        if (point >= 0 && (!km || point + 2 != number.Length || !Digits.AsSpan(1).Contains(number[^1]))) return null;
        if (integer != (km && point < 0 && whole == 2 ? "两" : FormattedNumber(whole))) return null;
        int durationStart = bridge + TimePrefix.Length;
        string duration = text[durationStart..^TimeSuffix.Length];
        if (!TryNumber(duration, out int minutes) || duration != (minutes == 2 ? "两" : FormattedNumber(minutes))) return null;
        string spoken = km && point < 0 && whole == 2 ? "两" : SpokenNumber(whole);
        if (point >= 0) spoken += number[point..];
        return new(heading, spoken, km, minutes == 2 ? "两" : SpokenNumber(minutes));
    }

    internal static string FormattedNumber(int n) => n <= 9999 ? BelowTenThousand(n).Replace("二百", "两百", StringComparison.Ordinal)
        : n.ToString(CultureInfo.InvariantCulture);

    private static bool TryNumber(string text, out int number)
    {
        number = 0;
        if (text.Length is 0 or > 16) return false;
        if (char.IsAsciiDigit(text[0])) return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
        int digit = 0;
        foreach (char c in text)
        {
            int d = c == '两' ? 2 : Digits.IndexOf(c);
            if (d >= 0) { digit = d; continue; }
            int scale = c switch { '十' => 10, '百' => 100, '千' => 1000, _ => 0 };
            if (scale == 0) return false;
            number += Math.Max(digit, 1) * scale;
            digit = 0;
        }
        number += digit;
        return number <= 9999;
    }

    // NavigationPrompts.Number keeps its 0..9999 contract. Larger numbers in a journey are
    // displayed as ASCII; a recording reads that number in groups of 万 and 亿.
    internal static string BelowTenThousand(int value)
    {
        if (value is < 0 or > 9999) throw new ArgumentOutOfRangeException(nameof(value));
        if (value == 0) return "零";
        var result = new StringBuilder();
        bool zero = false;
        for (int divisor = 1000; divisor > 0; divisor /= 10)
        {
            int digit = value / divisor;
            value %= divisor;
            if (digit == 0) { if (result.Length > 0 && value > 0) zero = true; continue; }
            if (zero) { result.Append('零'); zero = false; }
            if (!(divisor == 10 && digit == 1 && result.Length == 0)) result.Append(Digits[digit]);
            result.Append(divisor switch { 1000 => "千", 100 => "百", 10 => "十", _ => "" });
        }
        return result.ToString();
    }

    private static string SpokenNumber(int value)
    {
        if (value <= 9999) return FormattedNumber(value);
        var result = new StringBuilder();
        int priorScale = 0;
        foreach (int scale in new[] { 100000000, 10000, 1 })
        {
            int group = value / scale;
            value %= scale;
            if (group == 0) continue;
            if (priorScale > 0 && (priorScale / scale > 10000 || group < 1000)) result.Append('零');
            if (priorScale > 0 && group is >= 10 and < 20) result.Append('一');
            result.Append(FormattedNumber(group));
            result.Append(scale switch { 100000000 => "亿", 10000 => "万", _ => "" });
            priorScale = scale;
        }
        return result.ToString();
    }
}
