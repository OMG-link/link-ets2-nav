namespace LinkEts2Nav.Core.Guidance;

/// <summary>Places speech on the route when actions are discovered, never when they become current.</summary>
public sealed class NavigationEventPlanner
{
    private static readonly int[] Stages = [2000,1000,500,200,100];
    private sealed class ActionPlan(NavigationManeuver maneuver, string id, double along)
    {
        public NavigationManeuver Maneuver = maneuver;
        public readonly string Id = id;
        public readonly double Along = along;
        public double? EndAlong;
        public double? BriefingAlong;
        public bool BriefingStrict;
        public double Minimum = maneuver.DistanceMeters;
        public int Generation;
    }
    private readonly Dictionary<string,ActionPlan> actions = new(StringComparer.Ordinal);
    private readonly EnvironmentPreviewPlanner environment=new();
    private string? session, frame;
    private double? origin;
    public void Reset(){actions.Clear();environment.Reset();session=frame=null;origin=null;}

    public GuidanceSourcePlan Build(string sessionId,string routeRevision,DateTimeOffset observedAt,
        double playerAlong,IReadOnlyList<NavigationManeuver> maneuvers,bool active,bool deferred,
        bool confirmedContinuation,SpeechTriggerScheduler scheduler,string? coordinateFrame=null,
        double? initialBoundaryAlong=null,bool replaceRoute=false)
    {
        if(session!=sessionId||frame!=coordinateFrame){Reset();session=sessionId;frame=coordinateFrame;}
        origin ??= playerAlong;
        var incoming = maneuvers.Select(m=>m.Id).ToHashSet(StringComparer.Ordinal);
        if(replaceRoute)
            foreach(var key in actions.Where(p=>p.Value.Along>playerAlong&&!incoming.Contains(p.Key)).Select(p=>p.Key).ToArray())
                actions.Remove(key);
        foreach(var m in maneuvers)
        {
            if(!actions.TryGetValue(m.Id,out var state))
                actions[m.Id]=state=new(m,m.Id+"@0"+FrameSuffix(),playerAlong+m.DistanceMeters);
            else if(state.EndAlong is {} oldEnd&&playerAlong+m.DistanceMeters>oldEnd
                ||state.EndAlong is null&&state.Minimum<=20&&m.DistanceMeters>550)
            {
                int generation=state.Generation+1;
                actions[m.Id]=state=new(m,m.Id+"@"+generation+FrameSuffix(),playerAlong+m.DistanceMeters){Generation=generation};
            }
            state.Maneuver=m;
            state.Minimum=Math.Min(state.Minimum,m.DistanceMeters);
            if(m.EndDistanceMeters is {} end) state.EndAlong ??= playerAlong+end;
        }
        var ordered=actions.Values.OrderBy(p=>p.Along).ThenBy(p=>p.Maneuver.SpeechSequenceOrder).ToArray();
        var events=new List<GuidanceEvent>();var triggers=new List<SpeechTrigger>();
        var environmentEvents=new List<EnvironmentPreviewEvent>();
        var sequences=new Dictionary<string,SpeechSequence>(StringComparer.Ordinal);
        for(int i=0;i<ordered.Length;i++)
        {
            var state=ordered[i];var m=state.Maneuver;
            string sequence=m.SpeechSequenceId is null?state.Id:m.SpeechSequenceId+"@"+state.Generation+FrameSuffix();
            if(scheduler.IsConsumed("navigation",SpeechSequence.EndTriggerId(sequence)))continue;
            if(m.AnnouncementMode==NavigationAnnouncementMode.DistancePreview)
            {
                environmentEvents.Add(new(state.Id,"lane-reduction",state.Along,state.EndAlong,
                    meters=>meters>0?NavigationPrompts.AtDistance(m.Kind,meters,0,m.Instruction):NavigationPrompts.Execute(m.Kind,m.Instruction)));
                continue;
            }
            events.Add(new(state.Id,state.Along,m.Instruction??NavigationPrompts.Action(m.Kind)){SpeechSequenceId=sequence});
            if(m.IsMainRoadContinuation&&m.ForkInstruction?.Silent==true)
            {
                // Later map facts can prove a previously generic fork to be a
                // deceleration-lane split. Close its series, including queued audio.
                sequences[sequence]=new(sequence,state.EndAlong){Ended=true};
                continue;
            }
            if(!sequences.TryGetValue(sequence,out var previousSequence)||previousSequence.EndAlongMeters is null)
                sequences[sequence]=new(sequence,state.EndAlong);
            bool silent=m.AnnouncementMode==NavigationAnnouncementMode.Silent;
            if(m.Kind==ManeuverKind.Straight)
            {
                if(!m.IsMainRoadContinuation)
                    triggers.Add(new(state.Id+":passage",state.Id,SpeechTriggerKind.Passage,state.Along-50,"保持直行。")
                        {Enabled=active,SequenceOrder=0});
                continue;
            }
            ActionPlan? predecessor=ordered.Take(i).LastOrDefault(p=>IsNavigationAction(p.Maneuver));
            if(state.BriefingAlong is null)
            {
                // An unknown previous end is not the previous action's start.
                if(predecessor is null)
                {
                    state.BriefingAlong=Math.Max(playerAlong,initialBoundaryAlong??playerAlong);
                    state.BriefingStrict=initialBoundaryAlong is {} boundary&&state.BriefingAlong==boundary;
                }
                else if(predecessor.EndAlong is {} end)
                {
                    state.BriefingAlong=Math.Max(playerAlong,end);state.BriefingStrict=state.BriefingAlong==end;
                }
            }
            double? initial=state.BriefingAlong is {} anchor?Math.Max(0,state.Along-anchor):null;
            int priorIndex=predecessor is null?-1:Array.IndexOf(ordered,predecessor);
            var wording=ordered.Select(p=>p.Maneuver with{DistanceMeters=p.Along-playerAlong}).ToArray();
            string overviewId=state.Id+":overview";
            if(!m.ExecutionOnly&&initial is >=50)
            {
                int rounded=(int)NavigationPrompts.RoundDistance(initial.Value);
                string text=initial>800?NavigationPrompts.Continue(initial.Value)
                    :NavigationPrompts.FirstLayer(m.Kind,initial.Value,Ordinal(ordered,priorIndex+1,i,state.BriefingAlong!.Value),Instruction(m,rounded));
                triggers.Add(new(overviewId,state.Id,SpeechTriggerKind.Overview,state.BriefingAlong!.Value,text)
                    {Enabled=active,StrictCrossing=state.BriefingStrict,SequenceOrder=-rounded,Silent=silent,SkipAtSpeedKph=rounded==100?60:null});
            }
            int nearest=initial is {} distance?Stages.FirstOrDefault(s=>s<distance):0;
            foreach(int stage in Stages)
            {
                if(m.ExecutionOnly||initial is null||stage>=initial.Value
                    ||(initial<=800&&!Stages.Contains((int)initial.Value)&&stage==nearest))continue;
                double triggerAlong=state.Along-stage;
                string text=NavigationPrompts.AtDistance(m.Kind,stage,stage==200?Ordinal(ordered,priorIndex+1,i,triggerAlong):0,Instruction(m,stage));
                bool precedingStraight=ordered.Skip(priorIndex+1).Take(i-priorIndex-1)
                    .Any(p=>p.Along>=triggerAlong&&p.Maneuver.Kind==ManeuverKind.Straight&&!p.Maneuver.IsMainRoadContinuation);
                if(stage==100&&precedingStraight&&m.Kind!=ManeuverKind.EnterRoundabout)
                    text=text.TrimEnd('。')+"，前方路口直行。";
                triggers.Add(new(state.Id+":"+stage,state.Id,SpeechTriggerKind.Advance,state.Along-stage,text)
                    {Enabled=active,Silent=silent,SequenceOrder=-stage,SkipAtSpeedKph=stage==100?60:null});
            }
            triggers.Add(new(state.Id+":execute",state.Id,SpeechTriggerKind.Execution,state.Along-20,Execution(wording,i))
                {Enabled=active,Silent=silent,Priority=0,SequenceOrder=m.ExecutionOnly?m.SpeechSequenceOrder:0});
        }
        if(!maneuvers.Any(IsNavigationAction)&&confirmedContinuation)
        {
            var previous=ordered.LastOrDefault(p=>IsNavigationAction(p.Maneuver));
            string id="continuation:"+(previous?.Id??"origin"+FrameSuffix());
            double? anchor=previous?.EndAlong??(previous is null?initialBoundaryAlong??origin:null);
            if(anchor is {} start)
            {
                events.Add(new(id,start,"沿当前道路行驶"));
                triggers.Add(new(id+":overview",id,SpeechTriggerKind.Overview,start,NavigationPrompts.Continue()){Enabled=active});
            }
        }
        if(replaceRoute)environment.Reconcile(environmentEvents.Select(e=>e.Id).ToHashSet(StringComparer.Ordinal),playerAlong,scheduler,"navigation");
        var environmental=environment.Build("navigation",playerAlong,environmentEvents,scheduler,active);
        events.AddRange(environmental.Events);triggers.AddRange(environmental.Triggers);
        foreach(var sequence in environmental.Sequences)sequences[sequence.Id]=sequence;
        return new("navigation",sessionId,routeRevision,playerAlong,active,observedAt,events,triggers)
            {PrepareWhileInactive=active,CoordinateFrame=coordinateFrame,Sequences=sequences.Values.ToArray()};
    }
    private static bool IsNavigationAction(NavigationManeuver m)=>m.Kind!=ManeuverKind.Straight&&m.AnnouncementMode==NavigationAnnouncementMode.Normal;
    private string FrameSuffix()=>frame is null?"":"#"+frame;
    private static string? Instruction(NavigationManeuver m,int stage)=>stage is 0 or 100?m.NearInstruction??m.Instruction:m.Instruction;
    private static int ActionIndex(IReadOnlyList<NavigationManeuver> list,int start)
    {for(int i=start;i<list.Count;i++)if(IsNavigationAction(list[i]))return i;return -1;}
    private static int PreviousAction(IReadOnlyList<NavigationManeuver> list,int index)
    {for(int i=index-1;i>=0;i--)if(IsNavigationAction(list[i]))return i;return -1;}
    // Every phrase describes the route AFTER its own preplanned point, not the
    // whole leg or the player's position when the future audio was prepared.
    private static int Ordinal(IReadOnlyList<ActionPlan> list,int start,int end,double triggerAlong)
    {
        var target=list[end].Maneuver;
        if(target.Kind==ManeuverKind.EnterRoundabout||target.ForkInstruction is not null)return 0;
        var span=list.Skip(start).Take(end-start+1).Where(p=>p.Along>=triggerAlong).Select(p=>p.Maneuver).ToArray();
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
