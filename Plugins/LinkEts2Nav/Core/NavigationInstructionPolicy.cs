namespace LinkEts2Nav.Core;

/// <summary>Compatibility policy for standalone/offline callers. The host uses Guidance.NavigationEventPlanner.</summary>
public sealed class NavigationInstructionPolicy
{
    private const int MaximumEncounters = 2048;
    private static readonly int[] Stages = [2000,1000,500,200,100];
    private const int AllStages = 31;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StraightQuietPeriod = TimeSpan.FromSeconds(5);
    private sealed class Encounter(double distance)
    {
        public double Minimum = distance;
        public double? Previous;
        // One overview belongs to this approach, not to every changing distance sample.
        public string? PendingContinuation;
        public bool FirstAnnounced;
        public bool Executed;
        public bool ActionAnnounced;
        public bool OrdinalAnnounced;
        public int Consumed;
        public DateTimeOffset LastSeen;
    }
    private readonly record struct Decision(string Text,int Consumed,bool Execute=false,bool Overview=false,int? StageMeters=null);
    private readonly Dictionary<string,Encounter> encounters = new(StringComparer.Ordinal);
    private string? session;
    private DateTimeOffset? observed, lastSpoken;
    private bool continuationAnnounced;
    private string? legTargetId;

    public IReadOnlyList<string> Plan(NavigationManeuver? maneuver,string sessionId,
        DateTimeOffset observedAt,DateTimeOffset now,bool active)
        => PlanSequence(maneuver is null ? [] : [maneuver],sessionId,observedAt,now,active);
    public IReadOnlyList<string> Update(NavigationManeuver? maneuver,string sessionId,
        DateTimeOffset observedAt,DateTimeOffset now,bool active,Func<string,bool>? canAnnounce=null)
        => UpdateSequence(maneuver is null ? [] : [maneuver],sessionId,observedAt,now,active,canAnnounce);

    public IReadOnlyList<string> PlanSequence(IReadOnlyList<NavigationManeuver> maneuvers,string sessionId,
        DateTimeOffset observedAt,DateTimeOffset now,bool active,bool hasConfirmedContinuation=false)
    {
        if (!Valid(maneuvers,sessionId,observedAt,now,active,hasConfirmedContinuation)) return [];
        bool continuationCovered = StringComparer.Ordinal.Equals(session,sessionId)
            && continuationAnnounced && !StartsNewLeg(maneuvers);
        var result = new List<string>();
        int index = ActionIndex(maneuvers,0);
        if (index >= 0)
        {
            var target = maneuvers[index];
            if(target.AnnouncementMode!=NavigationAnnouncementMode.Normal)
                return target.AnnouncementMode==NavigationAnnouncementMode.Silent?[]:[NavigationPrompts.Execute(target.Kind,target.Instruction)];
            var state = Preview(target,sessionId);
            if (continuationCovered && !state.FirstAnnounced)
            {
                state = Copy(state);
                CoverOverview(state,target.DistanceMeters);
            }
            int ordinal = Ordinal(maneuvers,0,index);
            var due = Select(state,target,ordinal,maneuvers.Take(index).Any(m=>m.Kind==ManeuverKind.Straight && !m.IsMainRoadContinuation),ExecutionText(maneuvers,index));
            if (due is { } current) result.Add(current.Text);
            if (!target.ExecutionOnly && NeedsBriefing(state) && target.DistanceMeters <= 800)
            {
                // Warm the next two rounded bins, not an unbounded distance vocabulary.
                double rounded = Math.Floor(target.DistanceMeters/50)*50;
                foreach (double next in new[] { rounded-50,rounded-100 })
                    if (next >= 50) result.Add(NavigationPrompts.FirstLayer(target.Kind,next,ordinal,StageInstruction(target,(int)next)));
            }
            if(ordinal>1 && !state.OrdinalAnnounced && target.DistanceMeters>200)
                result.Add(StageText(target,200,ordinal));
            int consumed = due?.Consumed ?? state.Consumed;
            foreach (int stageIndex in Enumerable.Range(0,Stages.Length))
                if (!target.ExecutionOnly && (consumed & (1<<stageIndex)) == 0)
                    result.Add(StageText(target,Stages[stageIndex],ordinal, state.ActionAnnounced || due is { Overview:false },maneuvers.Take(index).Any(m=>m.Kind==ManeuverKind.Straight && !m.IsMainRoadContinuation)));
            if (!state.Executed)
            {
                result.Add(ExecutionText(maneuvers,index));
                result.Add(NavigationPrompts.Execute(target.Kind,StageInstruction(target,0)));
            }
            int nextIndex = ActionIndex(maneuvers,index+1);
            foreach(var execution in maneuvers.Skip(index+1).TakeWhile(m=>m.JunctionId==target.JunctionId).Where(m=>m.ExecutionOnly))
                result.Add(NavigationPrompts.Execute(execution.Kind,StageInstruction(execution,0)));
            if (nextIndex >= 0 && !maneuvers[nextIndex].ExecutionOnly && target.DistanceMeters <= 800
                && maneuvers[nextIndex].DistanceMeters - target.DistanceMeters <= 800)
            {
                var next = maneuvers[nextIndex];
                result.Add(NavigationPrompts.FirstLayer(next.Kind,
                    Math.Max(0,next.DistanceMeters-target.DistanceMeters),Ordinal(maneuvers,index+1,nextIndex),StageInstruction(next,(int)(Math.Floor(Math.Max(0,next.DistanceMeters-target.DistanceMeters)/50)*50))));
            }
        }
        else if (hasConfirmedContinuation && !continuationCovered && (maneuvers.Count==0 || maneuvers[0].DistanceMeters>50))
            result.Add(NavigationPrompts.Continue());
        var straight = maneuvers.Take(index >= 0 ? index : maneuvers.Count).FirstOrDefault(m => m.Kind==ManeuverKind.Straight && !m.IsMainRoadContinuation);
        if (straight is not null && !Preview(straight,sessionId).FirstAnnounced)
            result.Add(NavigationPrompts.Immediate(ManeuverKind.Straight));
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<string> UpdateSequence(IReadOnlyList<NavigationManeuver> maneuvers,string sessionId,
        DateTimeOffset observedAt,DateTimeOffset now,bool active,Func<string,bool>? canAnnounce=null,
        bool hasConfirmedContinuation=false,double speedMetersPerSecond=0)
    {
        if (!Valid(maneuvers,sessionId,observedAt,now,active,hasConfirmedContinuation)) return [];
        if (!StringComparer.Ordinal.Equals(session,sessionId)) { Reset(); session=sessionId; }
        bool newLeg=StartsNewLeg(maneuvers);
        if(newLeg) { continuationAnnounced=false; legTargetId=null; }
        int targetIndex=ActionIndex(maneuvers,0);
        if(targetIndex>=0 && (legTargetId is null || !maneuvers.Any(m=>m.Id==legTargetId)))
            legTargetId=maneuvers[targetIndex].Id;
        // Preserve the previous UPDATE distances until decisions have been made.
        var states = maneuvers.ToDictionary(m=>m.Id,m=>Current(m,now),StringComparer.Ordinal);
        var output = new List<string>(1);
        int index = ActionIndex(maneuvers,0);
        var passage=maneuvers.Take(index>=0 ? index : 0)
            .FirstOrDefault(m=>m.Kind==ManeuverKind.Straight && !m.IsMainRoadContinuation && m.DistanceMeters<=100);
        bool passageQuiet=lastSpoken is null || now-lastSpoken.Value>=StraightQuietPeriod;
        if(index>=0 && maneuvers[index].AnnouncementMode==NavigationAnnouncementMode.Normal && states[maneuvers[index].Id].FirstAnnounced && passage is not null && passageQuiet
            && !states[passage.Id].FirstAnnounced && maneuvers[index].DistanceMeters>100)
        {
            string text=NavigationPrompts.Immediate(ManeuverKind.Straight);
            if(canAnnounce?.Invoke(text)??true)
            { states[passage.Id].FirstAnnounced=true; output.Add(text); }
        }
        if (index >= 0 && output.Count==0)
        {
            var target = maneuvers[index];
            var state = states[target.Id];
            if(continuationAnnounced && !state.FirstAnnounced)
                CoverOverview(state,target.DistanceMeters);
            if (target.AnnouncementMode==NavigationAnnouncementMode.Normal && !state.FirstAnnounced && target.DistanceMeters > 800)
                state.PendingContinuation ??= NavigationPrompts.Continue(target.DistanceMeters);
            int ordinal=Ordinal(maneuvers,0,index);
            var due = Select(state,target,ordinal,maneuvers.Take(index).Any(m=>m.Kind==ManeuverKind.Straight && !m.IsMainRoadContinuation),ExecutionText(maneuvers,index));
            // Consume a skipped stage immediately, even if its audio is not ready.
            // Never apply this gate while preparing the speech plan.
            if(due is {StageMeters:100} && speedMetersPerSecond*3.6>=60)
                due=due.Value with{Text=""};
            bool ready=due is { } candidate && (candidate.Text.Length==0 || (canAnnounce?.Invoke(candidate.Text) ?? true));
            if(!ready && due is {Execute:true} execution && target.AnnouncementMode==NavigationAnnouncementMode.Normal)
            {
                // An unready compound sentence must never delay the current turn's execution cue.
                string bare=NavigationPrompts.Execute(target.Kind,StageInstruction(target,0));
                if(execution.Text!=bare && (canAnnounce?.Invoke(bare)??true))
                { due=execution with{Text=bare}; ready=true; }
            }
            if (target.AnnouncementMode==NavigationAnnouncementMode.Normal && !target.ExecutionOnly && !ready && NeedsBriefing(state) && target.DistanceMeters<=800 && (ordinal==1 || index==0))
            {
                // A prepared fixed boundary cue can replace a still-generating close briefing.
                // Never remove an intersection ordinal or announce a boundary not actually crossed.
                for(int i=2;i<Stages.Length;i++)
                {
                    int stage=Stages[i];
                    bool crossed=target.DistanceMeters==stage || state.Previous is { } previous
                        && previous>stage && target.DistanceMeters<=stage && target.DistanceMeters>=stage-20;
                    string text=NavigationPrompts.AtDistance(target.Kind,stage,0,StageInstruction(target,stage));
                    if(crossed && (stage==100 && speedMetersPerSecond*3.6>=60 || (canAnnounce?.Invoke(text) ?? true)))
                    {
                        due=new(stage==100 && speedMetersPerSecond*3.6>=60?"":text,state.Consumed|((1<<(i+1))-1),StageMeters:stage);
                        ready=true;
                        break;
                    }
                }
            }
            if (ready && due is { } current)
            {
                if (target.AnnouncementMode==NavigationAnnouncementMode.Silent) continuationAnnounced=false;
                else if (!state.FirstAnnounced && target.DistanceMeters>800) continuationAnnounced=true;
                state.FirstAnnounced=true;
                state.Consumed=current.Consumed;
                if(!current.Overview)state.ActionAnnounced=true;
                if(ordinal>1 && target.DistanceMeters<=200 && current.Text.Contains("个路口",StringComparison.Ordinal))
                    state.OrdinalAnnounced=true;
                if(current.Execute) state.Executed=true;
                if(current.Text.Length>0)output.Add(current.Text);
            }
        }
        // A silent or single-stage merge suppresses its own announcements,
        // not straight-through guidance at intervening junctions.
        if (output.Count == 0)
        {
            var straight = maneuvers.Take(index >= 0 ? index : maneuvers.Count)
                .FirstOrDefault(m=>m.Kind==ManeuverKind.Straight && !m.IsMainRoadContinuation);
            bool quiet=lastSpoken is null || now-lastSpoken.Value>=StraightQuietPeriod;
            if (quiet && straight is not null && straight.DistanceMeters <= 50 && !states[straight.Id].FirstAnnounced)
            {
                string text=NavigationPrompts.Immediate(ManeuverKind.Straight);
                if (canAnnounce?.Invoke(text) ?? true)
                { states[straight.Id].FirstAnnounced=true; output.Add(text); }
            }
            else if (hasConfirmedContinuation && index < 0 && !continuationAnnounced && (quiet || newLeg))
            {
                string text=NavigationPrompts.Continue();
                if (canAnnounce?.Invoke(text) ?? true) { continuationAnnounced=true; output.Add(text); }
            }
        }
        foreach (var m in maneuvers)
        {
            var state=states[m.Id];
            state.Previous=m.DistanceMeters;
            state.Minimum=Math.Min(state.Minimum,m.DistanceMeters);
            state.LastSeen=now;
        }
        observed=observedAt;
        if (output.Count > 0) lastSpoken=now;
        return output;
    }

    private static Decision? Select(Encounter state,NavigationManeuver target,int ordinal,bool precedingStraight,string executionText)
    {
        if(target.AnnouncementMode==NavigationAnnouncementMode.DistancePreview)
            return !state.Executed&&target.DistanceMeters<=500?new(NavigationPrompts.Execute(target.Kind,target.Instruction),AllStages,true):null;
        var decision=SelectNormal(state,target,ordinal,precedingStraight);
        if(decision is {Execute:true} && target.AnnouncementMode==NavigationAnnouncementMode.Normal)
            decision=decision.Value with{Text=executionText};
        return target.AnnouncementMode==NavigationAnnouncementMode.Silent&&decision is {} silent
            ?silent with {Text="",Overview=false}:decision;
    }
    private static Decision? SelectNormal(Encounter state,NavigationManeuver target,int ordinal,bool precedingStraight)
    {
        double distance=target.DistanceMeters;
        if(distance<=20) return state.Executed ? null : new(NavigationPrompts.Execute(target.Kind,StageInstruction(target,0)),AllStages,true);
        if(target.ExecutionOnly || distance<50) return null;
        if (NeedsBriefing(state))
            return new(distance > 800 && state.PendingContinuation is { } overview
                ? overview : NavigationPrompts.FirstLayer(target.Kind,distance,ordinal,StageInstruction(target,(int)(Math.Floor(distance/50)*50))),
                state.Consumed|InitialConsumption(distance),Overview:distance>800,
                StageMeters:distance<=800?(int)(Math.Floor(distance/50)*50):null);
        // A skipped 200 m distance reminder must not suppress the first near-junction ordinal.
        if(ordinal>1 && !state.OrdinalAnnounced && distance<=200 && distance>=180
            && (distance==200 || state.Previous is >200))
            return new(StageText(target,200,ordinal),state.Consumed|15);
        if (distance > Stages[0]) return null;
        int index=StageIndex(distance);
        if ((state.Consumed & (1<<index)) != 0) return null;
        int consumed=state.Consumed | ((1<<(index+1))-1);
        bool crossing=state.Previous.HasValue && distance>=Stages[index]-20;
        if (distance==Stages[index] || crossing)
            return new(StageText(target,Stages[index],ordinal,state.ActionAnnounced,precedingStraight),consumed,StageMeters:Stages[index]);
        if (distance<100) return null;
        // Missing a standard reminder does not authorize a new 400/150/50 m stage.
        // Leave later standards and the independent execution cue available.
        return null;
    }

    private static bool NeedsBriefing(Encounter state) => !state.FirstAnnounced;
    private static string StageText(NavigationManeuver target,int meters,int ordinal,bool hasPreviousAnnouncement=false,bool precedingStraight=false)
        => target.Kind==ManeuverKind.EnterRoundabout ? NavigationPrompts.AtDistance(target.Kind,meters,0,StageInstruction(target,meters))
            : meters==200 && ordinal>1 ? NavigationPrompts.AtDistance(target.Kind,meters,ordinal,StageInstruction(target,meters))
            : !hasPreviousAnnouncement ? NavigationPrompts.AtDistance(target.Kind,meters,ordinal,StageInstruction(target,meters))
            : precedingStraight && meters<=200 ? NavigationPrompts.AtDistance(target.Kind,meters,0,StageInstruction(target,meters)).TrimEnd('。')+"，前方路口直行。"
            : NavigationPrompts.AtDistance(target.Kind,meters,0,StageInstruction(target,meters));
    private static string ExecutionText(IReadOnlyList<NavigationManeuver> maneuvers,int index)
    {
        var target=maneuvers[index];
        string text=NavigationPrompts.Execute(target.Kind,StageInstruction(target,0));
        if(target.ExecutionOnly || target.Kind==ManeuverKind.EnterRoundabout
            || target.AnnouncementMode!=NavigationAnnouncementMode.Normal) return text;
        int nextIndex=ActionIndex(maneuvers,index+1);
        if(nextIndex<0) return text;
        var next=maneuvers[nextIndex];
        if(next.ExecutionOnly || next.AnnouncementMode!=NavigationAnnouncementMode.Normal
            || next.JunctionId is not null && next.JunctionId==target.JunctionId) return text;
        // Both execution points are 20 m before their junctions, so that offset
        // cancels. Keep this gap stable while preparing speech before either cue.
        double distance=next.DistanceMeters-target.DistanceMeters;
        if(distance<=0 || distance>=100) return text;
        int meters=(int)Math.Floor(distance/10)*10;
        return NavigationPrompts.Then(text,next.Kind,meters,next.Instruction);
    }

    private static string? StageInstruction(NavigationManeuver target,int meters)
        => meters is 0 or 100 ? target.NearInstruction??target.Instruction : target.Instruction;
    private static int InitialConsumption(double distance)
    {
        // A road-level overview does not replace the upcoming maneuver reminders.
        // Expire only boundaries already reached; e.g. 1.4 km keeps 1000/500/200/100 pending; execution has separate state.
        if (distance>800)
        {
            int expired=0;
            for(int i=0;i<Stages.Length;i++) if(Stages[i]>=distance) expired|=1<<i;
            return expired;
        }
        int index=StageIndex(distance);
        int consumed=(1<<(index+1))-1;
        if (distance<50) return AllStages;
        // An initial action briefing at a standard boundary replaces that same reminder.
        return distance==Stages[index] ? consumed
            : consumed | ((1<<Math.Min(index+2,Stages.Length))-1);
    }
    private static int StageIndex(double distance)
    {
        for (int i=Stages.Length-1;i>=0;i--) if (distance<=Stages[i]) return i;
        return 0;
    }
    private Encounter Preview(NavigationManeuver m,string sessionId)
        => StringComparer.Ordinal.Equals(session,sessionId) && encounters.TryGetValue(m.Id,out var state)
            && !(state.Minimum<=20 && m.DistanceMeters>550) ? state : new(m.DistanceMeters);
    private Encounter Current(NavigationManeuver m,DateTimeOffset now)
    {
        if (encounters.TryGetValue(m.Id,out var state))
        {
            if (state.Minimum<=20 && m.DistanceMeters>550) encounters[m.Id]=state=new(m.DistanceMeters);
            return state;
        }
        if (encounters.Count>=MaximumEncounters)
        {
            string oldest=encounters.OrderBy(p=>p.Value.LastSeen).ThenBy(p=>p.Key,StringComparer.Ordinal).First().Key;
            encounters.Remove(oldest);
        }
        encounters[m.Id]=state=new(m.DistanceMeters){LastSeen=now};
        return state;
    }
    private bool Valid(IReadOnlyList<NavigationManeuver> list,string sessionId,
        DateTimeOffset observedAt,DateTimeOffset now,bool active,bool hasConfirmedContinuation=false)
    {
        if (!active || now<observedAt || now-observedAt>MaximumAge || string.IsNullOrWhiteSpace(sessionId)
            || list.Count==0 && !hasConfirmedContinuation || StringComparer.Ordinal.Equals(session,sessionId) && observed is { } last && observedAt<last)
            return false;
        double previous=-1;var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var m in list)
        {
            if (m is null || string.IsNullOrWhiteSpace(m.Id) || !ids.Add(m.Id) || !Enum.IsDefined(m.Kind)
                || !double.IsFinite(m.DistanceMeters) || m.DistanceMeters<0 || m.DistanceMeters<previous) return false;
            previous=m.DistanceMeters;
        }
        return true;
    }
    private bool StartsNewLeg(IReadOnlyList<NavigationManeuver> list)
    {
        if(legTargetId is null || !encounters.TryGetValue(legTargetId,out var previous)
            || previous.Minimum>20) return false;
        var same=list.FirstOrDefault(m=>m.Id==legTargetId);
        return same is null || same.DistanceMeters>550;
    }

    private static Encounter Copy(Encounter original) => new(original.Minimum)
    {
        Previous=original.Previous,PendingContinuation=original.PendingContinuation,
        FirstAnnounced=original.FirstAnnounced,Executed=original.Executed,ActionAnnounced=original.ActionAnnounced,OrdinalAnnounced=original.OrdinalAnnounced,Consumed=original.Consumed,LastSeen=original.LastSeen
    };

    private static void CoverOverview(Encounter state,double distance)
    {
        state.FirstAnnounced=true;
        // Existing road-level guidance covers this target's initial overview at any distance.
        // Expire boundaries already behind the player, but leave future standards pending.
        for(int i=0;i<Stages.Length;i++) if(Stages[i]>distance) state.Consumed|=1<<i;
    }
    private static int ActionIndex(IReadOnlyList<NavigationManeuver> list,int start)
    {
        for(int i=start;i<list.Count;i++)if(list[i].Kind!=ManeuverKind.Straight)return i;
        return -1;
    }
    private static int Ordinal(IReadOnlyList<NavigationManeuver> list,int start,int end)
    {
        if(list[end].Kind==ManeuverKind.EnterRoundabout||list[end].ForkInstruction is not null)return 0;
        if(list.Skip(start).Take(end-start+1).Any(m=>!m.HasSeparateApproachRoad))return 0;
        return list.Skip(start).Take(end-start+1)
            .Select(m=>string.IsNullOrEmpty(m.JunctionId)?m.Id:m.JunctionId).Distinct(StringComparer.Ordinal).Count();
    }
    public void Reset()
    {
        encounters.Clear();session=null;observed=null;lastSpoken=null;continuationAnnounced=false;legTargetId=null;
    }
}
