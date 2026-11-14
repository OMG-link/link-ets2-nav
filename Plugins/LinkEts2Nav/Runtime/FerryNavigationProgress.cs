using System.Numerics;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.Runtime;

/// <summary>Position selects the already planned port; it never selects a destination by proximity.</summary>
internal sealed class FerryNavigationProgress
{
    private FerryJourneyIndex? index;
    private ulong[] routeNodes=[];
    private FerryRouteLeg? planned;
    private DateTimeOffset? entered;
    private long observedCompletion;
    private bool entryReported;
    private VehicleSnapshot? previous;
    public FerryTravelExpectation? Expected {get;private set;}
    public FerryRouteLeg? EnteredLeg=>entered is not null?planned:null;
    public void Reset(){index=null;routeNodes=[];planned=null;entered=null;Expected=null;entryReported=false;previous=null;}
    public string? TakeEntryPassage()
    {
        if(entered is null||planned is null||entryReported)return null;
        entryReported=true;return planned.Id;
    }

    public void Observe(GameDataSnapshot data,NearbyMapSnapshot nearby,DateTimeOffset now)
    {
        if(data.IsTransportPending)return;
        if(data.FerryArrival is {} completed&&completed.CurrentSessionId==data.SessionId&&completed.EventSequence!=observedCompletion)
        {Reset();observedCompletion=completed.EventSequence;return;}
        if(data.RouteAvailability==RouteAvailability.None&&data.Vehicle?.NavigationDistanceRemainingMeters==0)
        {Reset();return;}
        if(nearby.Ferries is null||data.Vehicle is not {SdkActive:true,IsFinite:true} vehicle
            ||now<vehicle.ReceivedAt||now-vehicle.ReceivedAt>TimeSpan.FromSeconds(2))return;
        var ids=RouteStructureIdentity.Nodes(data.Route);
        if(data.RouteAvailability==RouteAvailability.Available&&(!ReferenceEquals(index,nearby.Ferries)||!routeNodes.SequenceEqual(ids)))
        {
            index=nearby.Ferries;routeNodes=ids;
            var candidate=index.OnRoute(data.Route).FirstOrDefault();
            // The SDK drops the ferry records when the player books the crossing,
            // before placement moves. Preserve that entered, still-legal transfer.
            bool arrivalPrefix=entered is not null&&planned is not null&&planned.ExitUid!=0&&ids.FirstOrDefault()==planned.ExitUid;
            var goal=data.Route.MayBeTruncated?null:data.Route.Nodes.MinBy(n=>n.DistanceToEndMeters);
            bool sameGoal=planned?.GoalUid is null||goal?.Uid==planned.GoalUid;
            if(candidate is not null&&planned?.Id==candidate.Id)planned=candidate;
            else if(!arrivalPrefix||!sameGoal)
            {planned=candidate;entered=null;Expected=null;entryReported=false;}
        }
        if(planned is null)return;
        var position=new Vector3((float)vehicle.X,(float)vehicle.Y,(float)vehicle.Z);
        var areas=nearby.FreeNavigationAreas.Where(a=>a.Id==planned.Source.PrefabUid.ToString("X16")).ToArray();
        planned.Source.Entrances.TryGetValue(planned.EntranceUid,out var entrance);
        bool reversed=previous is not null&&entrance?.Crossed(previous,vehicle,false)==true;
        bool inside=areas.Any(a=>a.Contains(position))||!reversed&&(entered is not null||previous is not null&&entrance?.Crossed(previous,vehicle,true)==true);
        previous=vehicle;
        if(inside)
        {
            entered??=now;
            var arrival=nearby.FreeNavigationAreas.Where(a=>a.Id==planned.Target.PrefabUid.ToString("X16")).ToArray();
            Expected=new(planned,entered.Value,areas,arrival);
        }
        else if(entered is not null&&!vehicle.Paused)
        {entered=null;Expected=null;entryReported=false;}
    }
}
