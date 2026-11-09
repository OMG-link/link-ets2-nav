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
        return Distance(meters, round: false) + "后，" + (ordinal>1 ? "在第"+Number(ordinal)+"个路口" : "") + (instruction ?? Action(kind)) + "。";
    }

    public static string Execute(ManeuverKind kind, string? instruction = null) => (instruction ?? Action(kind)) + "。";

    public static string Immediate(ManeuverKind kind, string? instruction = null)
        => kind == ManeuverKind.Straight ? "保持直行。" : "即将" + (instruction ?? Action(kind)) + "。";

    // Zero ordinal means topology could not prove how many physical intersections lie ahead.
    public static string FirstLayer(ManeuverKind kind, double distance, int junctionOrdinal, string? instruction = null)
    {
        if (!double.IsFinite(distance) || distance < 0) throw new ArgumentOutOfRangeException(nameof(distance));
        if (distance > 800) return Continue(distance);
        int meters = (int)Math.Floor(distance / 50) * 50;
        if (meters < 50 && junctionOrdinal <= 1) return Immediate(kind,instruction);
        string where = junctionOrdinal <= 1 ? "" : "在第" + Number(junctionOrdinal) + "个路口";
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
            foreach (int distance in new[] { 2000,1000,500,450,400,350,300,250,200,150,100,50 })
                yield return AtDistance(kind,distance);
            yield return Immediate(kind);
            yield return Execute(kind);
        }
    }
}

/// <summary>Two-layer route guidance. Plan is read-only; only ready, actual announcements consume stages.</summary>
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
        public int Consumed;
        public DateTimeOffset LastSeen;
    }
    private readonly record struct Decision(string Text,int Consumed,bool Execute=false,bool Overview=false);
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
            var due = Select(state,target,ordinal,maneuvers.Take(index).Any(m=>m.Kind==ManeuverKind.Straight && !m.IsMainRoadContinuation));
            if (due is { } current) result.Add(current.Text);
            if (!target.ExecutionOnly && NeedsBriefing(state) && target.DistanceMeters <= 800)
            {
                // Warm the next two rounded bins, not an unbounded distance vocabulary.
                double rounded = Math.Floor(target.DistanceMeters/50)*50;
                foreach (double next in new[] { rounded-50,rounded-100 })
                    if (next >= 50) result.Add(NavigationPrompts.FirstLayer(target.Kind,next,ordinal,StageInstruction(target,(int)next)));
            }
            int consumed = due?.Consumed ?? state.Consumed;
            foreach (int stageIndex in Enumerable.Range(0,Stages.Length))
                if (!target.ExecutionOnly && (consumed & (1<<stageIndex)) == 0)
                    result.Add(StageText(target,Stages[stageIndex],ordinal, state.ActionAnnounced || due is { Overview:false },maneuvers.Take(index).Any(m=>m.Kind==ManeuverKind.Straight && !m.IsMainRoadContinuation)));
            if (!state.Executed) result.Add(NavigationPrompts.Execute(target.Kind,StageInstruction(target,0)));
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
        bool hasConfirmedContinuation=false)
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
            var due = Select(state,target,ordinal,maneuvers.Take(index).Any(m=>m.Kind==ManeuverKind.Straight && !m.IsMainRoadContinuation));
            bool ready=due is { } candidate && (candidate.Text.Length==0 || (canAnnounce?.Invoke(candidate.Text) ?? true));
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
                    if(crossed && (canAnnounce?.Invoke(text) ?? true))
                    {
                        due=new(text,state.Consumed|((1<<(i+1))-1));
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
                if(current.Execute) state.Executed=true;
                if(current.Text.Length>0)output.Add(current.Text);
            }
        }
        if (output.Count == 0 && (index<0 || maneuvers[index].AnnouncementMode==NavigationAnnouncementMode.Normal))
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

    private static Decision? Select(Encounter state,NavigationManeuver target,int ordinal,bool precedingStraight)
    {
        if(target.AnnouncementMode==NavigationAnnouncementMode.NoticeAt500)
            return !state.Executed&&target.DistanceMeters<=500?new(NavigationPrompts.Execute(target.Kind,target.Instruction),AllStages,true):null;
        var decision=SelectNormal(state,target,ordinal,precedingStraight);
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
                state.Consumed|InitialConsumption(distance),Overview:distance>800);
        if (distance > Stages[0]) return null;
        int index=StageIndex(distance);
        if ((state.Consumed & (1<<index)) != 0) return null;
        int consumed=state.Consumed | ((1<<(index+1))-1);
        bool crossing=state.Previous.HasValue && distance>=Stages[index]-20;
        if (distance==Stages[index] || crossing)
            return new(StageText(target,Stages[index],ordinal,state.ActionAnnounced,precedingStraight),consumed);
        if (distance<100) return null;
        // Missing a standard reminder does not authorize a new 400/150/50 m stage.
        // Leave later standards and the independent execution cue available.
        return null;
    }

    private static bool NeedsBriefing(Encounter state) => !state.FirstAnnounced;
    private static string StageText(NavigationManeuver target,int meters,int ordinal,bool hasPreviousAnnouncement=false,bool precedingStraight=false)
        => target.Kind==ManeuverKind.EnterRoundabout ? NavigationPrompts.AtDistance(target.Kind,meters,0,StageInstruction(target,meters))
            : !hasPreviousAnnouncement ? NavigationPrompts.AtDistance(target.Kind,meters,ordinal,StageInstruction(target,meters))
            : precedingStraight ? NavigationPrompts.AtDistance(target.Kind,meters,0,StageInstruction(target,meters)).TrimEnd('。')+"，前方路口直行。"
            : NavigationPrompts.AtDistance(target.Kind,meters,0,StageInstruction(target,meters));
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
        FirstAnnounced=original.FirstAnnounced,Executed=original.Executed,ActionAnnounced=original.ActionAnnounced,Consumed=original.Consumed,LastSeen=original.LastSeen
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
        if(list[end].Kind==ManeuverKind.EnterRoundabout)return 0;
        if(list.Skip(start).Take(end-start+1).Any(m=>!m.HasSeparateApproachRoad))return 0;
        return list.Skip(start).Take(end-start+1)
            .Select(m=>string.IsNullOrEmpty(m.JunctionId)?m.Id:m.JunctionId).Distinct(StringComparer.Ordinal).Count();
    }
    public void Reset()
    {
        encounters.Clear();session=null;observed=null;lastSpoken=null;continuationAnnounced=false;legTargetId=null;
    }
}
