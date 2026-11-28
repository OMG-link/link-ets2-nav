namespace LinkEts2Nav.Runtime;

internal sealed class NavigationHostSession(GuidanceCoordinator guidance, WarningPipeline warnings, NavigationRuntime navigation)
{
    private string? sessionId;

    internal bool Observe(GameDataSnapshot data, DateTimeOffset now, BackgroundMapScanner? scanner, bool processJourney = true)
    {
        // Guidance must see the arrival against the previous journey before the host drops spatial state.
        var route = guidance.ObserveFrame(data, now);
        bool changed = sessionId != data.SessionId;
        if (changed)
        {
            scanner?.Invalidate();
            warnings.Reset();
            navigation.Reset();
            sessionId = data.SessionId;
        }
        if (processJourney) guidance.UpdateJourney(data, now, route);
        return changed;
    }
}
