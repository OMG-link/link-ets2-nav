using System.Collections.Generic;

namespace ETS2LA.Game.Output;

/// <summary>The navigation development host never opens or writes vehicle control memory.</summary>
public class GameOutput
{
    private static readonly System.Lazy<GameOutput> instance = new(() => new GameOutput());
    public static GameOutput Current => instance.Value;
    public string EventString = "ETS2LA.Game.Output.ControlEvent";
    public Dictionary<string, ControlChannel> Channels = new();

    public void OnControlEvent(ControlEvent controlEvent) { }
    public void Tick() { }
}
