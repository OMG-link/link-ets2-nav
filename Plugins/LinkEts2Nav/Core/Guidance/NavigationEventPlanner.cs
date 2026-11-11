namespace LinkEts2Nav.Core.Guidance;

/// <summary>Expands the confirmed maneuver sequence into landmarks and upstream speech points.</summary>
public sealed class NavigationEventPlanner
{
    private static readonly int[] Stages=[2000,1000,500,200,100];
    private sealed class Encounter(int generation,double distance)
    { public int Generation=generation;public double Minimum=distance;public double? InitialDistance; }
    private readonly Dictionary<string,Encounter> encounters=new(StringComparer.Ordinal);
    private string? session,previousAction;
    private int leg;
    public void Reset(){encounters.Clear();session=previousAction=null;leg=0;}

    public GuidanceSourcePlan Build(string sessionId,string routeRevision,DateTimeOffset observedAt,
        double playerAlong,IReadOnlyList<NavigationManeuver> maneuvers,bool active,bool deferred,
        bool confirmedContinuation,SpeechTriggerScheduler scheduler)
    {
        if(session!=sessionId){Reset();session=sessionId;}
        int current=ActionIndex(maneuvers,0);
        string? next=current>=0?maneuvers[current].Id:null;
        if(previousAction is not null&&previousAction!=next)leg++;
        previousAction=next;
        var events=new List<GuidanceEvent>();var triggers=new List<SpeechTrigger>();
        bool continuationCovered=scheduler.IsConsumed("navigation","continuation:"+leg+":overview");
        for(int i=0;i<maneuvers.Count;i++)
        {
            var m=maneuvers[i];
            if(!encounters.TryGetValue(m.Id,out var state))encounters[m.Id]=state=new(0,m.DistanceMeters);
            else if(state.Minimum<=20&&m.DistanceMeters>550)encounters[m.Id]=state=new(state.Generation+1,m.DistanceMeters);
            state.Minimum=Math.Min(state.Minimum,m.DistanceMeters);
            string id=m.Id+"@"+state.Generation;
            double eventAlong=playerAlong+m.DistanceMeters;
            events.Add(new(id,eventAlong,m.Instruction??NavigationPrompts.Action(m.Kind)));
            bool isCurrent=i==current;
            bool enabled=active&&!deferred&&isCurrent;
            bool silent=m.AnnouncementMode==NavigationAnnouncementMode.Silent;
            if(m.Kind==ManeuverKind.Straight)
            {
                if(!m.IsMainRoadContinuation)
                    triggers.Add(new(id+":passage",id,SpeechTriggerKind.Passage,eventAlong-50,"保持直行。")
                        {Enabled=active&&!deferred&&(current<0||i<current),SequenceOrder=0});
                continue;
            }
            if(m.AnnouncementMode==NavigationAnnouncementMode.NoticeAt500)
            {
                triggers.Add(new(id+":500",id,SpeechTriggerKind.Advance,eventAlong-500,
                    NavigationPrompts.Execute(m.Kind,m.Instruction)){Enabled=enabled,SequenceOrder=-500});
                continue;
            }
            int previous=PreviousAction(maneuvers,i);
            double available=previous>=0?Math.Max(0,m.DistanceMeters-maneuvers[previous].DistanceMeters):m.DistanceMeters;
            if(enabled&&state.InitialDistance is null)state.InitialDistance=m.DistanceMeters;
            double initial=state.InitialDistance??available;
            int ordinal=Ordinal(maneuvers,previous+1,i);
            bool precedingStraight=maneuvers.Skip(previous+1).Take(i-previous-1)
                .Any(p=>p.Kind==ManeuverKind.Straight&&!p.IsMainRoadContinuation);
            string overviewId=id+":overview";
            bool covered=isCurrent&&continuationCovered;
            if(!m.ExecutionOnly&&initial>=50&&!covered)
            {
                int rounded=(int)NavigationPrompts.RoundDistance(initial);
                string text=initial>800?NavigationPrompts.Continue(initial)
                    :NavigationPrompts.FirstLayer(m.Kind,initial,ordinal,Instruction(m,rounded));
                triggers.Add(new(overviewId,id,SpeechTriggerKind.Overview,eventAlong-initial,text)
                    {Enabled=enabled,SequenceOrder=-rounded,Silent=silent,
                    SkipAtSpeedKph=rounded==100?60:null});
            }
            int nearest=Stages.FirstOrDefault(s=>s<initial);
            foreach(int stage in Stages)
            {
                if(m.ExecutionOnly||stage>=initial||(!covered&&initial<=800&&!Stages.Contains((int)initial)&&stage==nearest))continue;
                string text=NavigationPrompts.AtDistance(m.Kind,stage,stage==200?ordinal:0,Instruction(m,stage));
                if(stage==100&&precedingStraight&&m.Kind!=ManeuverKind.EnterRoundabout)
                    text=text.TrimEnd('。')+"，前方路口直行。";
                triggers.Add(new(id+":"+stage,id,SpeechTriggerKind.Advance,eventAlong-stage,text)
                    {Enabled=enabled,Silent=silent,SequenceOrder=-stage,SkipAtSpeedKph=stage==100?60:null,
                    Supersedes=Stages.Where(s=>s>stage).Select(s=>id+":"+s).Append(overviewId).ToArray()});
            }
            triggers.Add(new(id+":execute",id,SpeechTriggerKind.Execution,eventAlong-20,Execution(maneuvers,i))
                {Enabled=enabled,Silent=silent,Priority=0,SequenceOrder=0,
                Supersedes=Stages.Select(s=>id+":"+s).Append(overviewId).ToArray()});
        }
        if(current<0&&confirmedContinuation)
        {
            string id="continuation:"+leg;
            events.Add(new(id,playerAlong,"沿当前道路行驶"));
            triggers.Add(new(id+":overview",id,SpeechTriggerKind.Overview,playerAlong,NavigationPrompts.Continue())
                {Enabled=active&&!deferred,SequenceOrder=0});
        }
        return new("navigation",sessionId,routeRevision,playerAlong,active,observedAt,events,triggers)
            {PrepareWhileInactive=active};
    }
    private static string? Instruction(NavigationManeuver m,int stage)=>stage is 0 or 100?m.NearInstruction??m.Instruction:m.Instruction;
    private static int ActionIndex(IReadOnlyList<NavigationManeuver> list,int start)
    {for(int i=start;i<list.Count;i++)if(list[i].Kind!=ManeuverKind.Straight)return i;return -1;}
    private static int PreviousAction(IReadOnlyList<NavigationManeuver> list,int index)
    {for(int i=index-1;i>=0;i--)if(list[i].Kind!=ManeuverKind.Straight)return i;return -1;}
    private static int Ordinal(IReadOnlyList<NavigationManeuver> list,int start,int end)
    {
        if(list[end].Kind==ManeuverKind.EnterRoundabout)return 0;
        var span=list.Skip(start).Take(end-start+1).ToArray();
        if(span.Any(m=>!m.HasSeparateApproachRoad))return 0;
        return span.Select(m=>m.JunctionId??m.Id).Distinct(StringComparer.Ordinal).Count();
    }
    private static string Execution(IReadOnlyList<NavigationManeuver> list,int i)
    {
        var m=list[i];string text=NavigationPrompts.Execute(m.Kind,Instruction(m,0));
        if(m.ExecutionOnly||m.Kind==ManeuverKind.EnterRoundabout||m.AnnouncementMode!=NavigationAnnouncementMode.Normal)return text;
        int next=ActionIndex(list,i+1);if(next<0||list[next].ExecutionOnly||list[next].JunctionId==m.JunctionId)return text;
        double gap=list[next].DistanceMeters-m.DistanceMeters;
        if(gap<=0||gap>=100)return text;
        int meters=(int)(Math.Floor(gap/10)*10);
        return NavigationPrompts.Then(text,list[next].Kind,meters,list[next].Instruction);
    }
}
