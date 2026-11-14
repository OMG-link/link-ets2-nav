namespace LinkEts2Nav.Core.Guidance;

/// <summary>A directed environment landmark; dynamic coordinates are used only for Euclidean fallback.</summary>
public sealed record EnvironmentPreviewEvent(string Id,string Kind,double AlongMeters,double? EndAlongMeters,
    Func<int,string> Text,bool DynamicDistance=false,bool Ended=false,bool IncludeTwoHundred=true,double DistanceLowerBoundMeters=0,SpeechEndBoundary? PhysicalEnd=null);
public sealed record EnvironmentPreviewPlan(IReadOnlyList<SpeechSequence> Sequences)
{
    public IReadOnlyList<GuidanceEvent> Events=>Sequences.SelectMany(s=>s.Events).ToArray();
    public IReadOnlyList<SpeechTrigger> Triggers=>Sequences.SelectMany(s=>s.Triggers).ToArray();
}

/// <summary>500/200m points on independent same-kind chains. Each landmark remains its own playback series.</summary>
public sealed class EnvironmentPreviewPlanner
{
    private static readonly int[] Stages=[500,200];
    private sealed class State(EnvironmentPreviewEvent value,long order)
    {
        public EnvironmentPreviewEvent Value=value;
        public readonly long Order=order;
        public double? FirstAlong,FirstDistance;
        public bool FirstStrict;
        public bool NeedsRebase;
        public string? PredecessorId;
    }
    private readonly Dictionary<string,State> states=new(StringComparer.Ordinal);
    private long order;
    public void Reset(){states.Clear();order=0;}
    /// <summary>Translate shared plans into a new route frame without rediscovering their speech profile.</summary>
    public void Rebase(IReadOnlyList<EnvironmentPreviewEvent> input)
    {
        var current=input.ToDictionary(e=>e.Id,StringComparer.Ordinal);
        foreach(var (key,state) in states)
        {
            if(current.TryGetValue(key,out var item))Translate(state,item);
            else state.NeedsRebase=true;
        }
    }
    private static void Translate(State state,EnvironmentPreviewEvent item)
    {
        if(state.FirstAlong is {} first)state.FirstAlong=first+item.AlongMeters-state.Value.AlongMeters;
        state.Value=item;state.NeedsRebase=false;
    }
    public void Reconcile(IReadOnlySet<string> retained,double playerAlong,SpeechTriggerScheduler scheduler,string source)
    {
        foreach(var key in states.Where(p=>(p.Value.NeedsRebase||p.Value.Value.AlongMeters>playerAlong)&&!retained.Contains(p.Key)).Select(p=>p.Key).ToArray())
            states.Remove(key);
        foreach(var state in states.Values.Where(s=>!scheduler.IsConsumed(source,s.Value.Id+":first")))
            state.FirstAlong=state.FirstDistance=null;
    }
    public EnvironmentPreviewPlan Build(string source,double playerAlong,IReadOnlyList<EnvironmentPreviewEvent> input,
        SpeechTriggerScheduler scheduler,bool active)
    {
        foreach(var item in input)
        {
            if(!states.TryGetValue(item.Id,out var state))states[item.Id]=state=new(item,++order);
            else if(state.NeedsRebase)Translate(state,item);
            else state.Value=item with{PhysicalEnd=item.PhysicalEnd??state.Value.PhysicalEnd};
        }
        var visible=input.Select(e=>e.Id).ToHashSet(StringComparer.Ordinal);
        var events=new List<GuidanceEvent>();var triggers=new List<SpeechTrigger>();var ends=new Dictionary<string,(SpeechEndBoundary End,bool Ended)>();
        foreach(var chain in states.Values.Where(s=>!s.NeedsRebase).GroupBy(s=>s.Value.Kind))
        {
            State? previous=null;
            // Relative Euclidean coordinates cannot order two route landmarks.
            // Keep their proven discovery order instead of swapping predecessors
            // merely because one distance shrinks faster on a bend.
            bool relative=chain.Any(s=>s.Value.DynamicDistance);
            foreach(var state in chain.OrderBy(s=>relative?s.Order:s.Value.AlongMeters).ThenBy(s=>s.Order))
            {
                var item=state.Value;
                var predecessor=previous;
                if(predecessor is not null)state.PredecessorId=predecessor.Value.Id;
                else if(state.PredecessorId is {} priorId&&states.TryGetValue(priorId,out var prior)&&prior.NeedsRebase)
                    predecessor=prior;
                double? blocker=predecessor?.NeedsRebase==true?null:predecessor?.Value.EndAlongMeters;
                bool previousDone=predecessor is null||predecessor.Value.Ended
                    ||scheduler.IsConsumed(source,SpeechSequence.EndTriggerId(predecessor.Value.Id));
                // Newly discovered structure may invalidate an unconsumed first
                // point. Keep spoken profiles, but never keep a point before its
                // now-known predecessor's end (or invent an unknown end).
                if(!scheduler.IsConsumed(source,item.Id+":first")&&!previousDone&&state.FirstAlong is {} planned
                    &&(blocker is null&&predecessor?.NeedsRebase!=true||planned<blocker||planned==blocker&&!state.FirstStrict))
                    state.FirstAlong=state.FirstDistance=null;
                double distanceNow=item.AlongMeters-playerAlong;
                bool unknownFirst=item.DynamicDistance&&item.DistanceLowerBoundMeters>500&&item.DistanceLowerBoundMeters>distanceNow;
                if(unknownFirst&&!scheduler.IsConsumed(source,item.Id+":first"))state.FirstAlong=state.FirstDistance=null;
                if(state.FirstAlong is null&&!item.Ended&&!unknownFirst)
                {
                    if(previousDone||blocker is not null)
                    {
                        double start=Math.Max(playerAlong,item.AlongMeters-500);
                        if(!previousDone)start=Math.Max(start,blocker!.Value);
                        state.FirstStrict=!previousDone&&blocker==start;
                        state.FirstAlong=start;
                        state.FirstDistance=Math.Max(0,item.AlongMeters-start);
                    }
                }
                previous=state;
                if(!visible.Contains(item.Id)||scheduler.IsConsumed(source,SpeechSequence.EndTriggerId(item.Id)))continue;
                events.Add(new(item.Id,item.AlongMeters,item.Kind));
                var boundary=item.EndAlongMeters is {} along?new RouteSpeechEnd(along):item.PhysicalEnd;
                if(boundary is not null)ends[item.Id]=(boundary,item.Ended);
                if(item.Ended||unknownFirst||!previousDone&&blocker is null||state.FirstDistance is not {} distance)continue;
                bool normal=distance>=500;
                int meters=normal?500:(int)Math.Floor(distance/50)*50;
                double directedMinimum=playerAlong+Math.Max(distanceNow,item.DistanceLowerBoundMeters);
                double first=normal&&item.DynamicDistance?directedMinimum-500:state.FirstAlong!.Value;
                if(!previousDone)first=Math.Max(first,blocker!.Value);
                triggers.Add(new(item.Id+":first",item.Id,SpeechTriggerKind.Advance,first,item.Text(meters))
                    {Enabled=active,StrictCrossing=state.FirstStrict||!previousDone&&first==blocker,SequenceOrder=normal?-500:-meters});
                if(item.IncludeTwoHundred&&DistancePreviewStages.Includes(distance,200,Stages))
                {
                    double second=(item.DynamicDistance?directedMinimum:item.AlongMeters)-200;
                    if(!previousDone)second=Math.Max(second,blocker!.Value);
                    triggers.Add(new(item.Id+":200",item.Id,SpeechTriggerKind.Advance,second,item.Text(200))
                        {Enabled=active,StrictCrossing=!previousDone&&second==blocker,SequenceOrder=-200});
                }
            }
        }
        return new(events.Where(e=>ends.ContainsKey(e.Id)).Select(e=>new SpeechSequence(e.Id,ends[e.Id].End,[e],
            triggers.Where(t=>t.EventId==e.Id&&!ends[e.Id].Ended
                &&(ends[e.Id].End is not RouteSpeechEnd route||t.AlongMeters<=route.AlongMeters)).ToArray(),ends[e.Id].Ended)).ToArray());
    }
}
