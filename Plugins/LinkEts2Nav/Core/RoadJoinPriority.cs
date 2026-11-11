namespace LinkEts2Nav.Core;

public sealed record RoadJoinEntryMeasurement(ulong Entry,int LaneCount,double? IncomingSlopeDegrees,
    double? OutgoingSlopeDegrees,double? DirectionDifferenceDegrees,string? Failure=null)
{
    public double? SlopeDifferenceDegrees=>IncomingSlopeDegrees is {} a&&OutgoingSlopeDegrees is {} b?Math.Abs(a-b):null;
}
public sealed record RoadJoinEntryScore(RoadJoinEntryMeasurement Measurement,double SlopeScore,double DirectionScore,double LaneScore)
{
    public double Total=>SlopeScore+DirectionScore+LaneScore;
}
public sealed record RoadJoinPriority(ulong? MainEntry,IReadOnlyList<RoadJoinEntryMeasurement> Measurements,
    IReadOnlyList<RoadJoinEntryScore> Scores,double? WinningMargin,string Reason);

/// <summary>Scores only independent lane additions; real shared-lane merges do not use this policy.</summary>
public static class RoadJoinPriorityScorer
{
    private const double SlopeEpsilonDegrees=.1,DirectionEpsilonDegrees=.5,RequiredMargin=5;

    public static RoadJoinPriority Evaluate(IReadOnlyList<RoadJoinEntryMeasurement> entries)
    {
        RoadJoinPriority Unknown(string reason)=>new(null,entries,[],null,reason);
        if(entries.Count<2||entries.Select(e=>e.Entry).Distinct().Count()!=entries.Count)
            return Unknown("没有完整且唯一的竞争入口集合");
        bool Finite(double? value)=>value is {} v&&double.IsFinite(v);
        var missing=entries.Where(e=>e.LaneCount<=0||e.Failure is not null
            ||!Finite(e.IncomingSlopeDegrees)||!Finite(e.OutgoingSlopeDegrees)||!Finite(e.SlopeDifferenceDegrees)||!Finite(e.DirectionDifferenceDegrees)).ToArray();
        if(missing.Length>0)return Unknown("竞争入口数据不足："+string.Join("；",missing.Select(e=>$"{e.Entry:X16} {e.Failure??"坡度、方向或车道数缺失"}")));
        double SlopeWeight(RoadJoinEntryMeasurement e)=>1/Math.Max(e.SlopeDifferenceDegrees!.Value,SlopeEpsilonDegrees);
        double DirectionWeight(RoadJoinEntryMeasurement e)=>1/Math.Max(Math.Abs(e.DirectionDifferenceDegrees!.Value),DirectionEpsilonDegrees);
        double slopes=entries.Sum(SlopeWeight),directions=entries.Sum(DirectionWeight),lanes=entries.Sum(e=>(double)e.LaneCount);
        var scores=entries.Select(e=>new RoadJoinEntryScore(e,10*SlopeWeight(e)/slopes,10*DirectionWeight(e)/directions,10d*e.LaneCount/lanes))
            .OrderByDescending(s=>s.Total).ToArray();
        double margin=scores[0].Total-scores[1].Total;
        if(margin<RequiredMargin)return new(null,entries,scores,margin,$"最高分优势 {margin:F3}，不足 {RequiredMargin:F0} 分");
        return new(scores[0].Measurement.Entry,entries,scores,margin,$"主路领先全部其他入口至少 {margin:F3} 分");
    }
}
