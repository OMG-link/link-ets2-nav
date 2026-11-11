namespace LinkEts2Nav.Core.Guidance;

/// <summary>A directed environment landmark; dynamic coordinates are used only for Euclidean fallback.</summary>
public sealed record EnvironmentPreviewEvent(string Id,string Kind,double AlongMeters,double? EndAlongMeters,
    Func<int,string> Text,bool DynamicDistance=false,bool Ended=false,bool IncludeTwoHundred=true,double DistanceLowerBoundMeters=0);
public sealed record EnvironmentPreviewPlan(IReadOnlyList<GuidanceEvent> Events,IReadOnlyList<SpeechTrigger> Triggers,
    IReadOnlyList<SpeechSequence> Sequences);

/// <summary>500/200m points on independent same-kind chains. Each landmark remains its own playback series.</summary>
public sealed class EnvironmentPreviewPlanner
{
    private sealed class State(EnvironmentPreviewEvent value,long order)
    {
        public EnvironmentPreviewEvent Value=value;
        public readonly long Order=order;
        public double? FirstAlong,FirstDistance;
        public bool FirstStrict;
    }
    private readonly Dictionary<string,State> states=new(StringComparer.Ordinal);
    private long order;
    public void Reset(){states.Clear();order=0;}
    public void Reconcile(IReadOnlySet<string> retained,double playerAlong,SpeechTriggerScheduler scheduler,string source)
    {
        foreach(var key in states.Where(p=>p.Value.Value.AlongMeters>playerAlong&&!retained.Contains(p.Key)).Select(p=>p.Key).ToArray())
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
            else state.Value=item;
        }
        var visible=input.Select(e=>e.Id).ToHashSet(StringComparer.Ordinal);
        var events=new List<GuidanceEvent>();var triggers=new List<SpeechTrigger>();var sequences=new List<SpeechSequence>();
        foreach(var chain in states.Values.GroupBy(s=>s.Value.Kind))
        {
            State? previous=null;
            // Relative Euclidean coordinates cannot order two route landmarks.
            // Keep their proven discovery order instead of swapping predecessors
            // merely because one distance shrinks faster on a bend.
            bool relative=chain.Any(s=>s.Value.DynamicDistance);
            foreach(var state in chain.OrderBy(s=>relative?s.Order:s.Value.AlongMeters).ThenBy(s=>s.Order))
            {
                var item=state.Value;
                double distanceNow=item.AlongMeters-playerAlong;
                bool unknownFirst=item.DynamicDistance&&item.DistanceLowerBoundMeters>500&&item.DistanceLowerBoundMeters>distanceNow;
                if(unknownFirst&&!scheduler.IsConsumed(source,item.Id+":first"))state.FirstAlong=state.FirstDistance=null;
                if(state.FirstAlong is null&&!item.Ended&&!unknownFirst)
                {
                    double? blocker=previous?.Value.EndAlongMeters;
                    bool previousDone=previous is null||previous.Value.Ended
                        ||scheduler.IsConsumed(source,SpeechSequence.EndTriggerId(previous.Value.Id));
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
                sequences.Add(new(item.Id,item.EndAlongMeters,item.Ended));
                if(item.Ended||unknownFirst||state.FirstDistance is not {} distance)continue;
                bool normal=distance>=500;
                int meters=normal?500:(int)Math.Floor(distance/50)*50;
                double directedMinimum=playerAlong+Math.Max(distanceNow,item.DistanceLowerBoundMeters);
                double first=normal&&item.DynamicDistance?directedMinimum-500:state.FirstAlong!.Value;
                triggers.Add(new(item.Id+":first",item.Id,SpeechTriggerKind.Advance,first,item.Text(meters))
                    {Enabled=active,StrictCrossing=state.FirstStrict,SequenceOrder=normal?-500:-meters});
                if(normal&&item.IncludeTwoHundred)
                    triggers.Add(new(item.Id+":200",item.Id,SpeechTriggerKind.Advance,(item.DynamicDistance?directedMinimum:item.AlongMeters)-200,item.Text(200))
                        {Enabled=active,SequenceOrder=-200});
            }
        }
        return new(events,triggers,sequences);
    }
}
