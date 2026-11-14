namespace LinkEts2Nav.Core.Guidance;

/// <summary>Physical junction facts, separate from the chain of driving actions.</summary>
internal sealed record JunctionSpeechSite(string Id,string PhysicalId,double Along,double? EndAlong,double DiscoveredAlong,
    bool Straight,bool Minor,bool BreaksStraightChain);

/// <summary>Pure auxiliary planning. No playback history or second action lifecycle is stored here.</summary>
internal static class JunctionSpeechPlanner
{
    public static JunctionSpeechSite[] CountedStraights(IReadOnlyList<JunctionSpeechSite> route,
        double from,double target,string targetPhysicalId)
        =>route.Where(j=>j.Straight&&!j.Minor&&j.Along>=from&&j.Along<target&&j.PhysicalId!=targetPhysicalId)
            .DistinctBy(j=>j.PhysicalId).ToArray();

    public static IEnumerable<SpeechSequence> Numbered(string actionId,double countAlong,
        IReadOnlyList<JunctionSpeechSite> sites,bool enabled,bool strict)
    {
        for(int i=0;i<sites.Count;i++)
        {
            var site=sites[i];
            string text="直行通过第"+NavigationPrompts.Number(i+1)+"个路口。";
            if(Passage(actionId,site,countAlong,text,enabled,strict) is {} speech)yield return speech;
        }
    }

    public static IEnumerable<SpeechSequence> Current(string actionId,double earliest,
        IReadOnlyList<JunctionSpeechSite> sites,bool enabled,bool strict)
    {
        foreach(var site in sites)
            if(Passage(actionId,site,earliest,"直线通过当前路口。",enabled,strict) is {} speech)yield return speech;
    }

    private static SpeechSequence? Passage(string actionId,JunctionSpeechSite site,double earliest,string text,bool enabled,bool strict)
    {
        if(site.EndAlong is not {} end)return null;
        double point=Math.Max(earliest,site.Along-20);
        if(point>end)return null;
        string id=actionId+":through:"+site.Id;
        return new(id,new RouteSpeechEnd(end),[new(id,site.Along,text)],
            [new(id+":execute",id,SpeechTriggerKind.ActionAssist,point,text)
                {Enabled=enabled,StrictCrossing=strict&&point==earliest,
                    Window=new(earliest,context=>context.DistanceToEventMeters<=Math.Max(0,context.SpeedMetersPerSecond)*2,strict)}]);
    }

    public static IEnumerable<SpeechSequence> Dense(IReadOnlyList<JunctionSpeechSite> route,
        IReadOnlySet<string> counted,bool enabled,double? initialBoundary)
    {
        var chain=new List<JunctionSpeechSite>();
        bool previousAction=initialBoundary is not null;double? previousEnd=initialBoundary;
        foreach(var site in route)
        {
            if(site.BreaksStraightChain||site.Straight&&counted.Contains(site.Id))
            {if(Summary(chain,enabled,previousAction,previousEnd) is {} closed)yield return closed;chain.Clear();}
            if(site.BreaksStraightChain){previousAction=true;previousEnd=site.EndAlong;}
            if(!site.Straight||site.Minor||counted.Contains(site.Id))continue;
            if(chain.Count>0&&chain[^1].PhysicalId==site.PhysicalId)continue;
            if(chain.Count>0&&site.Along-chain[^1].Along>=200)
            {if(Summary(chain,enabled,previousAction,previousEnd) is {} closed)yield return closed;chain.Clear();}
            chain.Add(site);
        }
        if(Summary(chain,enabled,previousAction,previousEnd) is {} last)yield return last;
    }
    private static SpeechSequence? Summary(IReadOnlyList<JunctionSpeechSite> chain,bool enabled,bool previousAction,double? previousEnd)
    {
        if(chain.Count<3||chain[^3].EndAlong is not {} end)return null;
        string id="straight-chain:"+chain[0].Id;
        const string text="注意在前方多个路口保持直行。";
        double point=chain[0].Along-200;
        // This one-time summary belongs to its fixed 200m point; discovering
        // the third member later does not turn it into an immediate reminder.
        SpeechTrigger[] triggers=chain[2].DiscoveredAlong>point||previousAction&&(previousEnd is null||previousEnd>point)?[]:
            [new(id+":overview",id,SpeechTriggerKind.ActionAssist,point,text){Enabled=enabled,StrictCrossing=previousAction&&previousEnd==point}];
        return new(id,new RouteSpeechEnd(end),[new(id,chain[0].Along,text)],triggers);
    }
}
