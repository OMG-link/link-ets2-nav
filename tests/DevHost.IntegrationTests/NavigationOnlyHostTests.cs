using System.Runtime.CompilerServices;
using ETS2LA.Game;
using ETS2LA.Game.Output;
using ETS2LA.State;

namespace DevHost.IntegrationTests;

public sealed class NavigationOnlyHostTests
{
    // Avoid booting hardware listeners or global SDK readers in a regression test.
    private static ApplicationState State() =>
        (ApplicationState)RuntimeHelpers.GetUninitializedObject(typeof(ApplicationState));

    [Fact]
    public void AssistanceCannotBeEnabledByAnyCaller()
    {
        var state = State();
        state.EnableAssists = true;
        Assert.False(state.EnableAssists);
    }

    [Fact]
    public void DrivingModeCannotLeaveNavigationOnly()
    {
        var state = State();
        state.DrivingMode = DrivingMode.FullSelfDriving;
        Assert.Equal(DrivingMode.NavigationOnly, state.DrivingMode);
    }

    [Fact]
    public void TargetSpeedCannotActivateSpeedControl()
    {
        var state = State();
        state.DesiredSpeed = 25;
        Assert.Equal(0, state.DesiredSpeed);
    }

    [Fact]
    public void VehicleControlRequestsAreIgnored()
    {
        var output = new GameOutput();
        output.OnControlEvent(new ControlEvent
        {
            ChannelDefinition = new() { Id = "unexpected-driver" },
            Variables = new() { steering = .8f, aforward = 1, abackward = 1 }
        });
        output.Tick();
        Assert.Empty(output.Channels);
    }

    [Fact]
    public void ParsedMapInstallationRemainsAvailableToNavigation()
    {
        var state = State();
        var installation = new Installation
        {
            Type = GameType.EuroTruckSimulator2,
            Path = "game",
            DocumentsPath = "documents",
            ExecutablePath = "game.exe"
        };
        state.RunningGame = installation;
        Assert.Same(installation, state.RunningGame);
    }
}
