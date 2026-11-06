using ETS2LA.Backend;
using ETS2LA.Backend.Plugins;
using ETS2LA.Networking;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

// The host uses process-wide singletons, plugin shadow copies and output directories.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DevHost.IntegrationTests;

public sealed class LocalHostFixture : IDisposable
{
    private readonly string pluginDecoy;
    private readonly string libraryDecoy;
    public NetworkingClient Network { get; } = new();

    public LocalHostFixture()
    {
        var plugins = Path.Combine(AppContext.BaseDirectory, "Plugins");
        var libraries = Path.Combine(AppContext.BaseDirectory, "Libraries");
        Directory.CreateDirectory(plugins);
        Directory.CreateDirectory(libraries);
        if (!File.Exists(Path.Combine(plugins, "LinkEts2Nav.dll")))
            throw new FileNotFoundException("Build must copy the plugin under test into the test output.");
        pluginDecoy = Path.Combine(plugins, $"Unexpected-{Guid.NewGuid():N}.dll");
        libraryDecoy = Path.Combine(libraries, $"Unexpected-{Guid.NewGuid():N}.dll");
        File.WriteAllText(pluginDecoy, "This is not an assembly and must never load.");
        File.WriteAllText(libraryDecoy, "This is not an assembly and must never load.");
    }

    public void Dispose()
    {
        File.Delete(pluginDecoy);
        File.Delete(libraryDecoy);
    }
}

public sealed class LocalHostTests(LocalHostFixture fixture) : IClassFixture<LocalHostFixture>
{
    [Fact]
    public void ManualDiscovery_OnlyIncludesOwnPlugin()
    {
        var files = new PluginHandler().DiscoverManualDlls("Plugins");
        Assert.Equal("LinkEts2Nav.dll", Path.GetFileName(Assert.Single(files)));
    }

    [Fact]
    public void ManualDiscovery_ExcludesLibraries()
    {
        Assert.Empty(new PluginHandler().DiscoverManualDlls("Libraries"));
    }

    [Theory]
    [InlineData(PluginType.Plugin)]
    [InlineData(PluginType.Library)]
    public void ManifestDiscovery_IsDisabled(PluginType type)
    {
        Assert.Empty(new PluginHandler().DiscoverManifestDlls(type));
    }

    [Fact]
    public void PluginLoader_LoadsOwnPluginAndIgnoresDecoy()
    {
        var handler = new PluginHandler();
        try
        {
            handler.LoadPlugins();
            Assert.Equal("link.ets2.nav", Assert.Single(handler.LoadedPlugins).Info.Id);
        }
        finally
        {
            handler.UnloadPlugins();
        }
    }

    [Fact]
    public async Task RemoteCatalogue_RemainsEmpty()
    {
        await fixture.Network.Plugins.FetchAvailablePluginsAsync();
        Assert.Empty(fixture.Network.Plugins.AvailablePlugins);
    }

    [Fact]
    public void RemotePluginInstallation_IsRejected()
    {
        Assert.False(fixture.Network.Plugins.InstallPlugin("tumppi066.laneassist"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/start")]
    [InlineData("/plugins/catalogue")]
    public async Task DeveloperRoute_RendersLocalPage(string route)
    {
        var pages = typeof(ETS2LA.UI.UserInterface).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(RouteAttribute), false)
                .Cast<RouteAttribute>().Any(a => a.Template == route));
        var page = Assert.Single(pages);
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services,
            services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync(page, ParameterView.Empty);
            return rendered.ToHtmlString();
        });
        Assert.Contains("Link ETS2 Nav", html);
    }
}
