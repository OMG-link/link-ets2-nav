namespace LinkEts2Nav.Core;

/// <summary>Projects the shared primitive onto explicit incoming paths; never interprets lamps.</summary>
internal sealed record NativeMergeContinuity(bool? FirstEnds,bool? MoveLeft,bool AllDead,
    IReadOnlyList<int> SurvivingOrders,string Reason)
{
    internal static NativeMergeContinuity? Read(PrefabLanePath first,PrefabLanePath second,int common,
        LaneContinuityResult? analysis,IReadOnlyList<PrefabLanePath> all)
    {
        if(analysis is null||analysis.MapIssue is not null)
            return new(null,null,false,[],analysis?.MapIssue?.Reason??"缺少原生车道几何，无法判断合流生死");
        int shared=first.CurveIndices[^common];
        var point=analysis.Points.SingleOrDefault(p=>p.Kind==LaneContinuityPointKind.Merge&&p.SharedCurveIndex==shared);
        if(point is null||point.MapIssue is not null)
            return new(null,null,false,[],point?.MapIssue?.Reason??"局部合流点未形成完整原生判定");
        var a=point.Branches.SingleOrDefault(p=>p.CurveIndex==first.CurveIndices[first.CurveIndices.Count-common-1]);
        var b=point.Branches.SingleOrDefault(p=>p.CurveIndex==second.CurveIndices[second.CurveIndices.Count-common-1]);
        if(a is null||b is null||a.Role==LaneContinuityRole.Unknown||b.Role==LaneContinuityRole.Unknown)
            return new(null,null,false,[],"局部合流支路资格不完整");
        bool allDead=point.Branches.All(p=>p.Role==LaneContinuityRole.Dead);
        if(!allDead&&a.Role!=LaneContinuityRole.Survivor&&b.Role!=LaneContinuityRole.Survivor)return null;
        bool? firstEnds=allDead?null:a.Role==LaneContinuityRole.Dead;
        var ended=firstEnds==false?b:a;
        bool? left=allDead?null:ended.Turn switch
        {
            LaneTurnKind.ForwardLeft or LaneTurnKind.Left or LaneTurnKind.RearLeft=>true,
            LaneTurnKind.ForwardRight or LaneTurnKind.Right or LaneTurnKind.RearRight=>false,
            _=>null
        };
        var kept=all.Where(p=>p.PrefabUid==first.PrefabUid&&p.EntryNodeUid==first.EntryNodeUid)
            .Where(p=>p.CurveIndices.Contains(shared)).Where(p=>
            {
                int index=p.CurveIndices.ToList().IndexOf(shared);
                return index>0&&point.Branches.Any(branch=>branch.CurveIndex==p.CurveIndices[index-1]&&branch.Role==LaneContinuityRole.Survivor);
            }).Select(p=>p.EntryOrder).Distinct().Order().ToArray();
        return new(firstEnds,left,allDead,kept,allDead?"原生车道工具判定无唯一存活主线，全部支路均需并线":"原生车道工具确认死亡支及存活车道");
    }
}
