using System.Globalization;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.Runtime;

public static class NavigationJourneyPrompts
{
    public static string Format(NavigationJourneyNoticeKind kind,double worldMeters,double realSeconds)
    {
        if(!double.IsFinite(worldMeters)||worldMeters<0||!double.IsFinite(realSeconds)||realSeconds<0)
            throw new ArgumentOutOfRangeException(nameof(worldMeters));
        string distance;
        if(worldMeters<1000)
        {
            int meters=(int)Math.Round(worldMeters/10,MidpointRounding.AwayFromZero)*10;
            if(worldMeters>0)meters=Math.Max(10,meters);
            distance=Chinese(meters)+"米";
        }
        else
        {
            double km=Math.Round(worldMeters/1000,1,MidpointRounding.AwayFromZero);
            string[] parts=km.ToString("0.#",CultureInfo.InvariantCulture).Split('.');
            int whole=int.Parse(parts[0],CultureInfo.InvariantCulture);
            string kilometers=Chinese(whole);
            if(parts.Length>1)kilometers+="点"+Chinese(int.Parse(parts[1],CultureInfo.InvariantCulture));
            else if(whole==2)kilometers="两";
            distance=kilometers+"公里";
        }
        int minutes=checked((int)Math.Ceiling(realSeconds/60));
        if(worldMeters>0)minutes=Math.Max(1,minutes);
        string duration=minutes==2?"两":Chinese(minutes);
        return (kind==NavigationJourneyNoticeKind.Start?"准备出发":"重新规划路线")+"，全程"+distance+"，大约需要"+duration+"分钟。";
    }
    private static string Chinese(int n)=>n<=9999?NavigationPrompts.Number(n).Replace("二百","两百",StringComparison.Ordinal):n.ToString(CultureInfo.InvariantCulture);
}
