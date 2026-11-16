namespace LinkEts2Nav.Core;

/// <summary>Projects the shared primitive onto explicit incoming paths; never interprets lamps.</summary>
internal sealed record NativeMergeContinuity(bool? FirstEnds,bool? MoveLeft,bool AllDead,
    IReadOnlyList<int> SurvivingOrders,string Reason)
{
    internal bool AllIncomingLanesDie {get;init;}
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
        var known=analysis.UnchangedCurveIndices.Concat(analysis.Points.Select(p=>p.SharedCurveIndex))
            .Concat(analysis.Points.SelectMany(p=>p.Branches).Where(b=>b.Role!=LaneContinuityRole.Unknown).Select(b=>b.CurveIndex)).ToHashSet();
        (bool Survives,bool Dies) Continuation(PrefabLanePath path)
        {
            if(path.CurveIndices.Count==0||path.CurveIndices.Any(i=>!known.Contains(i)))return (false,false);
            bool survives=true,dies=false;
            for(int i=0;i<path.CurveIndices.Count;i++)
            foreach(var site in analysis.Points.Where(p=>p.SharedCurveIndex==path.CurveIndices[i]))
            {
                int at=site.Kind==LaneContinuityPointKind.Merge?i-1:i+1;
                if(at<0||at>=path.CurveIndices.Count)continue;
                var role=site.Branches.SingleOrDefault(b=>b.CurveIndex==path.CurveIndices[at])?.Role;
                if(site.MapIssue is not null||role is null or LaneContinuityRole.Unknown){survives=false;continue;}
                if(role!=LaneContinuityRole.Survivor)survives=false;
                if(site.Kind==LaneContinuityPointKind.Merge&&role==LaneContinuityRole.Dead)dies=true;
            }
            return (survives,dies);
        }
        // All lanes continuing along this carriageway matter, including lanes
        // untouched by this particular merge. Another outlet is a different choice.
        var groups=all.Where(p=>p.PrefabUid==first.PrefabUid&&p.EntryNodeUid==first.EntryNodeUid&&p.ExitNodeUid==first.ExitNodeUid
                &&p.EntryLaneCount==first.EntryLaneCount&&p.EntryOrder>=0&&p.EntryOrder<first.EntryLaneCount)
            .Select(p=>(p.EntryOrder,Evidence:Continuation(p))).GroupBy(p=>p.EntryOrder).ToArray();
        var kept=groups.Where(g=>g.Any(p=>p.Evidence.Survives)).Select(g=>g.Key).Order().ToArray();
        bool everyLaneDies=first.EntryLaneCount>0&&groups.Select(g=>g.Key).ToHashSet().SetEquals(Enumerable.Range(0,first.EntryLaneCount))
            &&groups.All(g=>g.All(p=>p.Evidence.Dies));
        return new(firstEnds,left,allDead,kept,allDead?"原生车道工具判定局部合流无唯一存活主线，参与支路均需并线":"原生车道工具确认死亡支及存活车道")
            {AllIncomingLanesDie=everyLaneDies};
    }
}
