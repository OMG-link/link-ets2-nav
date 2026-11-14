namespace LinkEts2Nav.Runtime;

/// <summary>A one-time fact, not a spatial series. The shared player still owns priority, caching and pause.</summary>
public sealed record GuidanceNotification(string Id,string Text,int Priority=5)
{
    /// <summary>Status notices may remain useful when position-dependent guidance is suspended.</summary>
    public bool AllowsStalePosition {get;init;}
}
