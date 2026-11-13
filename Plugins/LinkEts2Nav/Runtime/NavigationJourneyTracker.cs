namespace LinkEts2Nav.Runtime;

public sealed record NavigationJourneyUpdate(IReadOnlyList<GuidanceNotification> Notifications,
    bool WithdrawNotices=false,bool Ended=false);

/// <summary>Navigation lifecycle facts from fresh SDK snapshots, independent of map loading.</summary>
public sealed class NavigationJourneyTracker
{
    public const string Source="navigation-status";
    public const string StartText="导航开始，请按规划路线行驶。";
    public const string DestinationChangedText="目的地已更新，请按新的路线行驶。";
    public const string ReplannedText="路线已重新规划，请按新的路线行驶。";
    public const string ArrivedText="您已到达目的地，本次导航结束。";
    private static readonly NavigationJourneyUpdate Empty=new([]);
    private string? session;
    private RouteSnapshot? previous;
    private bool navigating;
    private int journey,notification;
    private double lastPositiveRemaining=double.PositiveInfinity;
    private DateTimeOffset? zeroSince,lastReplan;
    private int zeroSamples;
    private ulong? lastZeroSimulationTime;

    public NavigationJourneyUpdate Update(GameDataSnapshot data,DateTimeOffset now)
    {
        if(session!=data.SessionId){Reset();session=data.SessionId;}
        if(!data.IsVehicleUsable||data.Vehicle is not {SdkActive:true,Paused:false,IsFinite:true,SpeedMetersPerSecond:>=-.1} vehicle
            ||now<vehicle.ReceivedAt||now-vehicle.ReceivedAt>TimeSpan.FromSeconds(2))return Empty;
        double remaining=vehicle.NavigationDistanceRemainingMeters;
        if(remaining>0)
        {lastPositiveRemaining=remaining;zeroSince=null;zeroSamples=0;lastZeroSimulationTime=null;}
        else if(remaining==0)
        {
            zeroSince??=vehicle.ReceivedAt;
            if(lastZeroSimulationTime!=vehicle.SimulationTime){zeroSamples++;lastZeroSimulationTime=vehicle.SimulationTime;}
        }
        if(data.RouteAvailability==RouteAvailability.None&&remaining==0&&navigating)
        {
            bool near=lastPositiveRemaining<=200;
            bool confirmedZero=zeroSamples>=3&&zeroSince is {} since&&now-since>=TimeSpan.FromSeconds(1);
            // Cancellation at long range is not arrival. Near the goal, wait for
            // progressing telemetry instead of interpreting a transient zero as arrival.
            if(near&&!confirmedZero)return Empty;
            navigating=false;previous=null;
            return new(near?[Notice(ArrivedText)]:[],WithdrawNotices:true,Ended:true);
        }
        if(data.RouteAvailability!=RouteAvailability.Available||remaining<=0||data.Route.Nodes.Count==0
            ||now<data.Route.ReceivedAt||now-data.Route.ReceivedAt>TimeSpan.FromSeconds(3))return Empty;
        var route=data.Route;
        if(!navigating)
        {
            navigating=true;journey++;notification=0;previous=route;lastReplan=null;
            return new([Notice(StartText)],WithdrawNotices:true);
        }
        var old=previous;previous=route;
        if(old is null||old.Revision==route.Revision)return Empty;
        if(Goal(old) is {} oldGoal&&Goal(route) is {} goal&&oldGoal!=goal)
        {
            journey++;notification=0;lastReplan=null;
            return new([Notice(DestinationChangedText)],WithdrawNotices:true);
        }
        if(RouteContinuity.IsForwardExtension(old,route))return Empty;
        // A replacement UID list does not expose why it changed. Use neutral
        // language for deviations, waypoint edits and other same-goal recalculations.
        if(lastReplan is {} replanned&&now-replanned<TimeSpan.FromSeconds(5))return Empty;
        lastReplan=now;
        return new([Notice(ReplannedText)],WithdrawNotices:true);
    }
    private GuidanceNotification Notice(string text)=>new($"journey:{journey}:{++notification}",text);
    private static ulong? Goal(RouteSnapshot route)=>!route.MayBeTruncated&&route.Nodes.MinBy(n=>n.DistanceToEndMeters) is {DistanceToEndMeters:<=.01f} node?node.Uid:null;
    public void Reset()
    {
        session=null;previous=null;navigating=false;journey=notification=zeroSamples=0;
        lastPositiveRemaining=double.PositiveInfinity;zeroSince=lastReplan=null;lastZeroSimulationTime=null;
    }
}
