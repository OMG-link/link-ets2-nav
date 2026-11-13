namespace LinkEts2Nav.Runtime;

public enum NavigationJourneyNoticeKind { Start, Replanned }
public sealed record NavigationJourneyNotice(string Id,NavigationJourneyNoticeKind Kind);
public sealed record NavigationJourneyUpdate(NavigationJourneyNotice? Notice=null,bool WithdrawNotices=false,bool Ended=false);

/// <summary>Navigation lifecycle facts. Wording waits for a complete world-distance/time estimate.</summary>
public sealed class NavigationJourneyTracker
{
    public const string Source="navigation-status";
    private static readonly NavigationJourneyUpdate Empty=new();
    private string? session;
    private RouteSnapshot? previous;
    private bool navigating;
    private int journey,notification;
    private DateTimeOffset? lastReplan;
    public NavigationJourneyUpdate Update(GameDataSnapshot data,DateTimeOffset now)
    {
        if(session!=data.SessionId){Reset();session=data.SessionId;}
        if(!data.IsVehicleUsable||data.Vehicle is not {SdkActive:true,Paused:false,IsFinite:true,SpeedMetersPerSecond:>=-.1} vehicle
            ||now<vehicle.ReceivedAt||now-vehicle.ReceivedAt>TimeSpan.FromSeconds(2))return Empty;
        if(data.RouteAvailability==RouteAvailability.None&&vehicle.NavigationDistanceRemainingMeters==0&&navigating)
        {
            navigating=false;previous=null;
            return new(WithdrawNotices:true,Ended:true); // No independent arrival announcement.
        }
        if(data.RouteAvailability!=RouteAvailability.Available||vehicle.NavigationDistanceRemainingMeters<=0||data.Route.Nodes.Count==0
            ||now<data.Route.ReceivedAt||now-data.Route.ReceivedAt>TimeSpan.FromSeconds(3))return Empty;
        var route=data.Route;
        if(!navigating)
        {
            navigating=true;journey++;notification=0;previous=route;lastReplan=null;
            return new(Notice(NavigationJourneyNoticeKind.Start),WithdrawNotices:true);
        }
        var old=previous;previous=route;
        if(old is null||old.Revision==route.Revision)return Empty;
        bool goalChanged=Goal(old) is {} oldGoal&&Goal(route) is {} goal&&oldGoal!=goal;
        if(!goalChanged&&RouteContinuity.IsForwardExtension(old,route))return Empty;
        // Goal edits and all other route replacements use the same notice and cooldown.
        if(lastReplan is {} replanned&&now-replanned<TimeSpan.FromSeconds(5))return Empty;
        lastReplan=now;
        return new(Notice(NavigationJourneyNoticeKind.Replanned),WithdrawNotices:true);
    }
    private NavigationJourneyNotice Notice(NavigationJourneyNoticeKind kind)=>new($"journey:{journey}:{++notification}",kind);
    private static ulong? Goal(RouteSnapshot route)=>!route.MayBeTruncated&&route.Nodes.MinBy(n=>n.DistanceToEndMeters) is {DistanceToEndMeters:<=.01f} node?node.Uid:null;
    public void Reset(){session=null;previous=null;navigating=false;journey=notification=0;lastReplan=null;}
}
