using TruckLib.ScsMap;
namespace LinkEts2Nav.Map;

public enum RoadNameStatus { Found, Unnamed, Unresolved }
public enum RoadLabelKind { Name, RouteNumber }
/// <summary>Evidence must explicitly bind this label to an item; nearby sign text is not such a binding.</summary>
public sealed record RoadNameEvidence(ulong ItemUid,RoadNameStatus Status,string Source,
    string? Text=null,RoadLabelKind Kind=RoadLabelKind.Name,ulong? EntryNodeUid=null,ulong? ExitNodeUid=null);
public sealed record RoadNameResult(ulong ItemUid,RoadNameStatus Status,IReadOnlyList<RoadNameEvidence> Labels,string Reason);

/// <summary>
/// Queries loaded physical roads/prefabs, preserving multiple directions and explicit uncertainty.
/// TruckLib's current Road/Prefab fields have no road-name channel. SignBoards.Road is a label token,
/// not an owning road UID. An independently verified binding can be supplied without changing callers.
/// </summary>
public sealed class RoadNameIndex
{
    private readonly HashSet<ulong> items;
    private readonly Dictionary<ulong,RoadNameEvidence[]> evidence;
    public RoadNameIndex(IEnumerable<MapItem> loadedItems,IEnumerable<RoadNameEvidence>? verifiedBindings=null)
    {
        items=loadedItems.Where(i=>i is Road or Prefab).Select(i=>i.Uid).ToHashSet();
        var bindings=(verifiedBindings??[]).ToArray();
        if(bindings.Any(e=>e.Status==RoadNameStatus.Unresolved||string.IsNullOrWhiteSpace(e.Source)
            ||e.Status==RoadNameStatus.Found&&string.IsNullOrWhiteSpace(e.Text)
            ||e.Status==RoadNameStatus.Unnamed&&e.Text is not null))
            throw new ArgumentException("Named/unnamed claims require explicit evidence and consistent text.",nameof(verifiedBindings));
        evidence=bindings.GroupBy(e=>e.ItemUid).ToDictionary(g=>g.Key,g=>g.ToArray());
    }
    public RoadNameResult Query(ulong itemUid)
    {
        if(!items.Contains(itemUid))return new(itemUid,RoadNameStatus.Unresolved,[],"ItemNotLoadedOrNotRoadOrPrefab");
        if(!evidence.TryGetValue(itemUid,out var labels))return new(itemUid,RoadNameStatus.Unresolved,[],"NativeRoadNameNotExposedAndNoVerifiedSignBinding");
        if(labels.Select(e=>e.Status).Distinct().Count()>1)return new(itemUid,RoadNameStatus.Unresolved,labels,"ConflictingNameEvidence");
        return new(itemUid,labels[0].Status,labels,"ExplicitItemBinding");
    }
}
