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
    private readonly NavigationRouteCursor routes = new();
    private long? previousContinuity;
    private bool navigating;
    private int journey,notification;
    private DateTimeOffset? lastReplan;
    public NavigationJourneyUpdate Update(GameDataSnapshot data,DateTimeOffset now)
    {
        EnsureSession(data.SessionId);
        return Update(data, now, routes.Observe(data));
    }
    internal NavigationJourneyUpdate Update(GameDataSnapshot data,DateTimeOffset now,NavigationRouteState route)
    {
        EnsureSession(data.SessionId);
        if(!data.IsVehicleUsable||data.Vehicle is not {SdkActive:true,Paused:false,IsFinite:true,SpeedMetersPerSecond:>=-.1} vehicle
            ||now<vehicle.ReceivedAt||now-vehicle.ReceivedAt>TimeSpan.FromSeconds(2))return Empty;
        if(data.RouteAvailability==RouteAvailability.None&&vehicle.NavigationDistanceRemainingMeters==0&&navigating)
        {
            navigating=false;previousContinuity=null;
            return new(WithdrawNotices:true,Ended:true); // No independent arrival announcement.
        }
        if(data.RouteAvailability!=RouteAvailability.Available||vehicle.NavigationDistanceRemainingMeters<=0||data.Route.Nodes.Count==0
            ||now<data.Route.ReceivedAt||now-data.Route.ReceivedAt>TimeSpan.FromSeconds(3))return Empty;
        if(!navigating)
        {
            navigating=true;journey++;notification=0;previousContinuity=route.ContinuityGeneration;lastReplan=null;
            return new(Notice(NavigationJourneyNoticeKind.Start),WithdrawNotices:true);
        }
        var old=previousContinuity;previousContinuity=route.ContinuityGeneration;
        if(old is null||old==previousContinuity)return Empty;
        // Goal edits and all other route replacements use the same notice and cooldown.
        if(lastReplan is {} replanned&&now-replanned<TimeSpan.FromSeconds(5))return Empty;
        lastReplan=now;
        return new(Notice(NavigationJourneyNoticeKind.Replanned),WithdrawNotices:true);
    }
    private NavigationJourneyNotice Notice(NavigationJourneyNoticeKind kind)=>new($"journey:{journey}:{++notification}",kind);
    /// <summary>A verified transport changes placement, not the driver's navigation journey.</summary>
    public bool ResumeAfterFerry(string newSession)
    {
        if(!navigating)return false;
        session=newSession;routes.ResumeAfterFerry(newSession);lastReplan=null;return true;
    }
    private void EnsureSession(string next)
    {
        if (session == next) return;
        Reset(); session = next;
    }
    public void Reset(){session=null;previousContinuity=null;routes.Reset();navigating=false;journey=notification=0;lastReplan=null;}
}
