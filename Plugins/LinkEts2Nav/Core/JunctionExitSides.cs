namespace LinkEts2Nav.Core;

/// <summary>
/// Classifies other outlets relative to a Main/Natural outlet. Exit facts come
/// from ComponentContinuityIndex.Exits: native, player-permitted through-traffic
/// passages, rather than every port present on the prefab. This query neither
/// counts junctions nor decides whether a passage needs a spoken instruction.
/// </summary>
public static class JunctionExitSides
{
    public static NavigationSide OtherSides(IReadOnlyList<ComponentPassageContinuity> exitFacts,
        ulong prefabUid,ulong entryNodeUid,ulong selectedExitNodeUid)
    {
        var approach=exitFacts.Where(f=>f.PrefabUid==prefabUid&&f.EntryNodeUid==entryNodeUid).ToArray();
        Validate(approach,prefabUid,entryNodeUid,selectedExitNodeUid);
        var selected=approach.Single(f=>f.ExitNodeUid==selectedExitNodeUid);
        NavigationSide sides=NavigationSide.None;
        foreach(var other in approach.Where(f=>f.ExitNodeUid!=selectedExitNodeUid))
        {
            sides|=other.Kind switch
            {
                ComponentContinuityKind.Main or ComponentContinuityKind.Natural=>InheritedSide(selected,other),
                ComponentContinuityKind.Hard or ComponentContinuityKind.Soft
                    when other.Issue is null&&other.Turn is {Kind:not ComponentTurnKind.Unknown,
                        Side:NavigationSide.Left or NavigationSide.Right} turn=>turn.Side,
                _=>throw new NotSupportedException($"Prefab {prefabUid:X16}, entrance {entryNodeUid:X16}, outlet {other.ExitNodeUid:X16}: 出口没有已确立的主侧关系或转向结果，无法依据现有用例及已确立规则类推处理方式。")
            };
        }
        return sides;
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void Validate(IReadOnlyList<ComponentPassageContinuity> approach,ulong prefab,ulong entry,ulong exit)
    {
        var matches=approach.Where(f=>f.ExitNodeUid==exit).ToArray();
        if(matches.Length!=1||matches[0].Kind is not (ComponentContinuityKind.Main or ComponentContinuityKind.Natural)
            ||matches[0].Issue is not null||matches[0].EntryOrders.Count==0
            ||approach.Select(f=>f.ExitNodeUid).Distinct().Count()!=approach.Count)
            throw new NavigationContractException($"Prefab {prefab:X16}, entrance {entry:X16}: selected outlet {exit:X16} must be a unique Main/Natural exit fact with nonempty inherited entry lanes.");
    }

    private static NavigationSide InheritedSide(ComponentPassageContinuity selected,ComponentPassageContinuity other)
    {
        if(other.Issue is null&&other.EntryOrders.Count>0)
        {
            if(other.EntryOrders.Max()<selected.EntryOrders.Min())return NavigationSide.Left;
            if(other.EntryOrders.Min()>selected.EntryOrders.Max())return NavigationSide.Right;
        }
        throw new NotSupportedException($"Prefab {selected.PrefabUid:X16}, entrance {selected.EntryNodeUid:X16}, outlets {selected.ExitNodeUid:X16}/{other.ExitNodeUid:X16}: 继承车道集合 [{string.Join(',',selected.EntryOrders)}]/[{string.Join(',',other.EntryOrders)}] 交错、重叠、相同或缺少比较基准，无法依据现有用例及已确立规则类推处理方式。");
    }
}
