using ETS2LA.Logging;
using ETS2LA.Shared;

namespace LinkEts2Nav;

/// <summary>Initial plugin lifecycle check; navigation and TTS are not implemented yet.</summary>
public sealed class NavPlugin : Plugin
{
    public override PluginInformation Info => new PluginInformation
    {
        Id = "link.ets2.nav",
        Version = "0.1.0",
        Name = "Link ETS2 Nav",
        Description = "Route-aware navigation and speed-camera warnings (development scaffold).",
        AuthorName = "Link",
    };

    public override float TickRate => 1f;

    public override void Init()
    {
        base.Init();
        Logger.Info("[[LinkEts2Nav]] Plugin loaded.");
    }

    public override void OnEnable()
    {
        base.OnEnable();
        Logger.Info("[[LinkEts2Nav]] Plugin enabled.");
    }

    public override void OnDisable()
    {
        Logger.Info("[[LinkEts2Nav]] Plugin disabled.");
        base.OnDisable();
    }
}
