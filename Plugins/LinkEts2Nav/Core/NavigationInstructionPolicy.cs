using System.Globalization;

namespace LinkEts2Nav.Core;

public static class NavigationPrompts
{
    public static string Action(ManeuverKind kind) => kind switch
    {
        ManeuverKind.TurnLeft => "左转", ManeuverKind.TurnRight => "右转",
        ManeuverKind.KeepLeft => "向左前方行驶", ManeuverKind.KeepRight => "向右前方行驶",
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

    public static string AtDistance(ManeuverKind kind, int meters)
    {
        if (meters is < 50 or > 2000 || meters % 50 != 0)
            throw new ArgumentOutOfRangeException(nameof(meters));
        return "前方" + Number(meters) + "米，" + Action(kind) + "。";
    }

    public static string Immediate(ManeuverKind kind)
        => kind == ManeuverKind.Straight ? "保持直行。" : "即将" + Action(kind) + "。";

    // Zero ordinal means topology could not prove how many physical intersections lie ahead.
    public static string FirstLayer(ManeuverKind kind, double distance, int junctionOrdinal)
    {
        if (!double.IsFinite(distance) || distance < 0) throw new ArgumentOutOfRangeException(nameof(distance));
        if (distance > 800) return Continue(distance);
        int meters = (int)Math.Floor(distance / 50) * 50;
        if (meters < 50 && junctionOrdinal <= 1) return Immediate(kind);
        string where = junctionOrdinal <= 0 ? "" : junctionOrdinal == 1
            ? "在下一个路口" : "在第" + Number(junctionOrdinal) + "个路口";
        return (meters >= 50 ? Number(meters) + "米后，" : "") + where + Action(kind) + "。";
    }

    public static string Continue(double distance)
        => "沿当前道路，继续行驶" + Distance(distance) + "。";
    public static string Continue() => "沿当前道路继续行驶。";

    private static string Distance(double distance)
    {
        if (!double.IsFinite(distance) || distance < 50) throw new ArgumentOutOfRangeException(nameof(distance));
        double meters = Math.Floor(distance / 50) * 50;
        if (meters < 1000) return Number((int)meters) + "米";
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
        }
    }
}

/// <summary>Two-layer route guidance. Plan is read-only; only ready, actual announcements consume stages.</summary>
public sealed class NavigationInstructionPolicy
{
    private const int MaximumEncounters = 2048;
    private static readonly int[] Stages = [2000,1000,500,200,50];
    private const int AllStages = 31;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StraightQuietPeriod = TimeSpan.FromSeconds(5);
    private sealed class Encounter(double distance)
    {
        public double Minimum = distance;
        public double? Previous;
        public bool FirstAnnounced;
        public bool FirstWasContinuation;
        public bool CloseBriefed;
        public int Consumed;
        public DateTimeOffset LastSeen;
    }
    private readonly record struct Decision(string Text,int Consumed);
    private readonly Dictionary<string,Encounter> encounters = new(StringComparer.Ordinal);
    private string? session;
    private DateTimeOffset? observed, lastSpoken;
    private bool continuationAnnounced;

    public IReadOnlyList<string> Plan(NavigationManeuver? maneuver,string sessionId,
        DateTimeOffset observedAt,DateTimeOffset now,bool active)
        => PlanSequence(maneuver is null ? [] : [maneuver],sessionId,observedAt,now,active);
    public IReadOnlyList<string> Update(NavigationManeuver? maneuver,string sessionId,
        DateTimeOffset observedAt,DateTimeOffset now,bool active,Func<string,bool>? canAnnounce=null)
        => UpdateSequence(maneuver is null ? [] : [maneuver],sessionId,observedAt,now,active,canAnnounce);

    public IReadOnlyList<string> PlanSequence(IReadOnlyList<NavigationManeuver> maneuvers,string sessionId,
        DateTimeOffset observedAt,DateTimeOffset now,bool active)
    {
        if (!Valid(maneuvers,sessionId,observedAt,now,active)) return [];
        var result = new List<string>();
        int index = ActionIndex(maneuvers,0);
        if (index >= 0)
        {
            var target = maneuvers[index];
            var state = Preview(target,sessionId);
            int ordinal = Ordinal(maneuvers,0,index);
            var due = Select(state,target,ordinal);
            if (due is { } current) result.Add(current.Text);
            if (NeedsBriefing(state,target.DistanceMeters))
            {
                // Warm the next two rounded bins, not an unbounded distance vocabulary.
                double rounded = Math.Floor(target.DistanceMeters/50)*50;
                foreach (double next in new[] { rounded-50,rounded-100 })
                    if (next >= 0) result.Add(NavigationPrompts.FirstLayer(target.Kind,next,ordinal));
            }
            int nextIndex = ActionIndex(maneuvers,index+1);
            if (nextIndex >= 0)
            {
                var next = maneuvers[nextIndex];
                result.Add(NavigationPrompts.FirstLayer(next.Kind,
                    Math.Max(0,next.DistanceMeters-target.DistanceMeters),Ordinal(maneuvers,index+1,nextIndex)));
            }
            int consumed = due?.Consumed ?? state.Consumed;
            foreach (int stageIndex in Enumerable.Range(0,Stages.Length))
                if ((consumed & (1<<stageIndex)) == 0)
                    result.Add(StageText(target.Kind,Stages[stageIndex],ordinal));
            if (consumed != AllStages) result.Add(ImmediateText(target.Kind,ordinal));
        }
        else if (!(StringComparer.Ordinal.Equals(session,sessionId) && continuationAnnounced)
            && maneuvers[0].DistanceMeters > 50)
            result.Add(NavigationPrompts.Continue());
        var straight = maneuvers.Take(index >= 0 ? index : maneuvers.Count).FirstOrDefault(m => m.Kind==ManeuverKind.Straight);
        if (straight is not null && !Preview(straight,sessionId).FirstAnnounced)
            result.Add(NavigationPrompts.Immediate(ManeuverKind.Straight));
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<string> UpdateSequence(IReadOnlyList<NavigationManeuver> maneuvers,string sessionId,
        DateTimeOffset observedAt,DateTimeOffset now,bool active,Func<string,bool>? canAnnounce=null)
    {
        if (!Valid(maneuvers,sessionId,observedAt,now,active)) return [];
        if (!StringComparer.Ordinal.Equals(session,sessionId)) { Reset(); session=sessionId; }
        // Preserve the previous UPDATE distances until decisions have been made.
        var states = maneuvers.ToDictionary(m=>m.Id,m=>Current(m,now),StringComparer.Ordinal);
        var output = new List<string>(1);
        int index = ActionIndex(maneuvers,0);
        if (index >= 0)
        {
            var target = maneuvers[index];
            var state = states[target.Id];
            var due = Select(state,target,Ordinal(maneuvers,0,index));
            if (due is { } current && (canAnnounce?.Invoke(current.Text) ?? true))
            {
                if (NeedsBriefing(state,target.DistanceMeters))
                {
                    if (!state.FirstAnnounced) state.FirstWasContinuation=target.DistanceMeters>800;
                    if (target.DistanceMeters<=800) state.CloseBriefed=true;
                }
                state.FirstAnnounced=true;
                state.Consumed=current.Consumed;
                continuationAnnounced=false;
                output.Add(current.Text);
            }
        }
        if (output.Count == 0 && (lastSpoken is null || now-lastSpoken.Value >= StraightQuietPeriod))
        {
            var straight = maneuvers.Take(index >= 0 ? index : maneuvers.Count)
                .FirstOrDefault(m=>m.Kind==ManeuverKind.Straight);
            if (straight is not null && straight.DistanceMeters <= 50 && !states[straight.Id].FirstAnnounced)
            {
                string text=NavigationPrompts.Immediate(ManeuverKind.Straight);
                if (canAnnounce?.Invoke(text) ?? true)
                { states[straight.Id].FirstAnnounced=true; output.Add(text); }
            }
            else if (index < 0 && !continuationAnnounced)
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

    private static Decision? Select(Encounter state,NavigationManeuver target,int ordinal)
    {
        double distance=target.DistanceMeters;
        if (NeedsBriefing(state,distance))
            return new(NavigationPrompts.FirstLayer(target.Kind,distance,ordinal),state.Consumed|InitialConsumption(state,distance));
        if (distance > Stages[0]) return null;
        int index=StageIndex(distance);
        if ((state.Consumed & (1<<index)) != 0) return null;
        int consumed=state.Consumed | ((1<<(index+1))-1);
        int pending=0;while ((state.Consumed & (1<<pending)) != 0) pending++;
        bool crossing=index==pending && state.Previous is { } previous && previous>Stages[index]
            && distance>=Stages[index]-20;
        if (distance==Stages[index] || crossing)
            return new(StageText(target.Kind,Stages[index],ordinal),consumed);
        if (distance<50) return new(ImmediateText(target.Kind,ordinal),AllStages);
        consumed|=(1<<Math.Min(index+2,Stages.Length))-1;
        return new(StageText(target.Kind,(int)Math.Floor(distance/50)*50,ordinal),consumed);
    }

    private static bool NeedsBriefing(Encounter state,double distance)
        => !state.FirstAnnounced || state.FirstWasContinuation && !state.CloseBriefed && distance<=800;
    private static string StageText(ManeuverKind kind,int meters,int ordinal)
        => meters<=800 && ordinal>1 ? NavigationPrompts.FirstLayer(kind,meters,ordinal)
            : NavigationPrompts.AtDistance(kind,meters);
    private static string ImmediateText(ManeuverKind kind,int ordinal)
        => ordinal>1 ? NavigationPrompts.FirstLayer(kind,0,ordinal) : NavigationPrompts.Immediate(kind);
    private static int InitialConsumption(Encounter state,double distance)
    {
        if (distance>Stages[0]) return 0;
        int index=StageIndex(distance);
        int consumed=(1<<(index+1))-1;
        if (distance<50) return AllStages;
        // An actual initial briefing at a standard boundary replaces that same reminder.
        bool crossing=index==0 && state.Previous is { } previous && previous>Stages[index]
            && distance>=Stages[index]-20;
        return distance==Stages[index] || crossing ? consumed
            : consumed | ((1<<Math.Min(index+2,Stages.Length))-1);
    }
    private static int StageIndex(double distance)
    {
        for (int i=Stages.Length-1;i>=0;i--) if (distance<=Stages[i]) return i;
        return 0;
    }
    private Encounter Preview(NavigationManeuver m,string sessionId)
        => StringComparer.Ordinal.Equals(session,sessionId) && encounters.TryGetValue(m.Id,out var state)
            && !(state.Minimum<=50 && m.DistanceMeters>550) ? state : new(m.DistanceMeters);
    private Encounter Current(NavigationManeuver m,DateTimeOffset now)
    {
        if (encounters.TryGetValue(m.Id,out var state))
        {
            if (state.Minimum<=50 && m.DistanceMeters>550) encounters[m.Id]=state=new(m.DistanceMeters);
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
        DateTimeOffset observedAt,DateTimeOffset now,bool active)
    {
        if (!active || now<observedAt || now-observedAt>MaximumAge || string.IsNullOrWhiteSpace(sessionId)
            || list.Count==0 || StringComparer.Ordinal.Equals(session,sessionId) && observed is { } last && observedAt<last)
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
    private static int ActionIndex(IReadOnlyList<NavigationManeuver> list,int start)
    {
        for(int i=start;i<list.Count;i++)if(list[i].Kind!=ManeuverKind.Straight)return i;
        return -1;
    }
    private static int Ordinal(IReadOnlyList<NavigationManeuver> list,int start,int end)
    {
        if(list.Skip(start).Take(end-start+1).Any(m=>!m.HasSeparateApproachRoad))return 0;
        return list.Skip(start).Take(end-start+1)
            .Select(m=>string.IsNullOrEmpty(m.JunctionId)?m.Id:m.JunctionId).Distinct(StringComparer.Ordinal).Count();
    }
    public void Reset()
    {
        encounters.Clear();session=null;observed=null;lastSpoken=null;continuationAnnounced=false;
    }
}
