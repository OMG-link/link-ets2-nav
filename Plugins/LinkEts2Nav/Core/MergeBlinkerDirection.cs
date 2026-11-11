namespace LinkEts2Nav.Core;

public sealed record MergeBlinkerDecision(bool? FirstEnds,bool? MoveLeft,string Reason);

/// <summary>
/// The competing incoming curves immediately before a shared downstream chain own
/// the lamp commands at this merge. Earlier approach turns and downstream lamps do
/// not describe this conflict. Topology and lateral-position validation are callers' responsibility.
/// </summary>
public static class MergeBlinkerDirection
{
    public static MergeBlinkerDecision Classify(PrefabLanePath first,PrefabLanePath second,int sharedSuffixCount)
    {
        if(sharedSuffixCount<=0||sharedSuffixCount>=Math.Min(first.CurveIndices.Count,second.CurveIndices.Count)
            ||first.CurveBlinkers.Count!=first.CurveIndices.Count||second.CurveBlinkers.Count!=second.CurveIndices.Count)
            return new(null,null,"合流前原生转向灯数据不可用");
        var a=first.CurveBlinkers[^(sharedSuffixCount+1)];
        var b=second.CurveBlinkers[^(sharedSuffixCount+1)];
        bool None(LaneBlinker lamp)=>lamp is LaneBlinker.NoBlinker or LaneBlinker.NoBlinkerForced;
        bool Turn(LaneBlinker lamp)=>lamp is LaneBlinker.Left or LaneBlinker.Right;
        if(Turn(a)&&None(b))return new(true,a==LaneBlinker.Left,"合流前单侧原生转向灯；保留无转向灯路线");
        if(None(a)&&Turn(b))return new(false,b==LaneBlinker.Left,"合流前单侧原生转向灯；保留无转向灯路线");
        return new(null,null,$"合流前原生转向灯不能唯一判定：{a}/{b}");
    }
}
