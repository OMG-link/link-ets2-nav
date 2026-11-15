namespace LinkEts2Nav.Core;

/// <summary>Formats driver-relative lane selections from native lane counts and left-to-right orders.</summary>
public static class LaneInstructionFormatter
{
    public static string? Selection(int count,IReadOnlyList<int> selected)
    {
        var lanes=selected.Distinct().Order().ToArray();
        if(count<1||lanes.Length==0||lanes.Any(l=>l<0||l>=count)||lanes.Length==count)return null;
        bool contiguous=lanes[^1]-lanes[0]+1==lanes.Length;
        if(contiguous&&(lanes[0]==0||lanes[^1]==count-1))
        {
            string side=lanes[0]==0?"左":"右";
            if(lanes.Length==1)return "走"+(count==2?"":"最")+side+"侧车道";
            return "走"+side+"侧"+(lanes.Length==2?"两":NavigationPrompts.Number(lanes.Length))+"车道";
        }
        return "走右侧第"+string.Join("、第",lanes.Select(l=>count-l).Order().Select(NavigationPrompts.Number))+"车道";
    }
}
