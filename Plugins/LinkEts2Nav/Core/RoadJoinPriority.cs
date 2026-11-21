using LinkEts2Nav.Map;
namespace LinkEts2Nav.Core;

public sealed record RoadJoinEntryMeasurement(ulong Entry,int LaneCount,double? IncomingSlopeDegrees,
    double? OutgoingSlopeDegrees,double? DirectionDifferenceDegrees,string? Failure=null)
{
    public double SlopeWeightFactor { get; init; }=1;
    public double? SlopeDifferenceDegrees=>IncomingSlopeDegrees is {} a&&OutgoingSlopeDegrees is {} b?Math.Abs(a-b):null;
}
public sealed record RoadJoinEntryScore(RoadJoinEntryMeasurement Measurement,double SlopeScore,double DirectionScore,double LaneScore)
{
    public double Total=>SlopeScore+DirectionScore+LaneScore;
}
public sealed record RoadJoinPriority(ulong? MainEntry,IReadOnlyList<RoadJoinEntryMeasurement> Measurements,
    IReadOnlyList<RoadJoinEntryScore> Scores,double? WinningMargin,string Reason)
{
    public IReadOnlyList<MapQueryIssue> MapIssues {get;init;}=[];
    public bool NoEligibleOutlet {get;init;}
}

/// <summary>Shared thirty-point priority scorer for component entrance and outlet candidates.</summary>
public static class RoadJoinPriorityScorer
{
    private const double SlopeEpsilonDegrees=.1,DirectionEpsilonDegrees=.5,RequiredMargin=5;

    public static RoadJoinPriority Evaluate(IReadOnlyList<RoadJoinEntryMeasurement> entries)
    {
#if DEBUG
        if(entries.Count==0||entries.Select(e=>e.Entry).Distinct().Count()!=entries.Count)
            throw new NavigationContractException($"Component priority requires at least one unique candidate. Received [{string.Join(", ",entries.Select(e=>e.Entry.ToString("X16")))}].");
        foreach(var entry in entries)
            if(entry.LaneCount<=0)
                throw new NavigationContractException($"Component priority candidate {entry.Entry:X16} has {entry.LaneCount} lanes; at least one is required.");
#endif
        if(entries.Count==1)
            return new(entries[0].Entry,entries,[],null,"Only one component priority candidate.");
        RoadJoinPriority Unknown(string reason)=>new(null,entries,[],null,reason);
        bool Finite(double? value)=>value is {} v&&double.IsFinite(v);
        var missing=entries.Where(e=>e.Failure is not null||!Finite(e.DirectionDifferenceDegrees)).ToArray();
        if(missing.Length>0)return Unknown("竞争入口数据不足："+string.Join("；",missing.Select(e=>$"{e.Entry:X16} {e.Failure??"方向缺失"}")));
        double SlopeWeight(RoadJoinEntryMeasurement e)
        {
            double? difference=e.SlopeDifferenceDegrees;
            if(!double.IsFinite(e.SlopeWeightFactor)||e.SlopeWeightFactor<0||e.SlopeWeightFactor>1
                || e.IncomingSlopeDegrees is not null&&!Finite(e.IncomingSlopeDegrees)
                || e.OutgoingSlopeDegrees is not null&&!Finite(e.OutgoingSlopeDegrees)
                || difference is not null&&!Finite(difference)
                || e.SlopeWeightFactor>0&&difference is null)
                throw new NotSupportedException($"Component port {e.Entry:X16}: cannot score slope from measurements {e.IncomingSlopeDegrees}, {e.OutgoingSlopeDegrees} and weight factor {e.SlopeWeightFactor}.");
            return e.SlopeWeightFactor==0?0:e.SlopeWeightFactor/Math.Max(difference!.Value,SlopeEpsilonDegrees);
        }
        double DirectionWeight(RoadJoinEntryMeasurement e)=>1/Math.Max(Math.Abs(e.DirectionDifferenceDegrees!.Value),DirectionEpsilonDegrees);
        var slopeWeights=entries.Select(SlopeWeight).ToArray();
        double slopes=slopeWeights.Sum(),directions=entries.Sum(DirectionWeight),lanes=entries.Sum(e=>(double)e.LaneCount);
        var scores=entries.Select((e,i)=>new RoadJoinEntryScore(e,slopes>0?10*slopeWeights[i]/slopes:0,10*DirectionWeight(e)/directions,10d*e.LaneCount/lanes))
            .OrderByDescending(s=>s.Total).ToArray();
        double margin=scores[0].Total-scores[1].Total;
        if(margin<RequiredMargin)return new(null,entries,scores,margin,$"最高分优势 {margin:F3}，不足 {RequiredMargin:F0} 分");
        return new(scores[0].Measurement.Entry,entries,scores,margin,$"主路领先全部其他入口至少 {margin:F3} 分");
    }
}
