using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using System.ComponentModel;
using System.Diagnostics;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;

// Inspect generated event bindings: static HTML rendering cannot distinguish a literal @onclick.
#pragma warning disable BL0006
public sealed class DiagnosticsInteractionTests
{
    private sealed class Page : LinkEts2Nav.Pages.Diagnostics
    {
        public void Render(RenderTreeBuilder builder) => BuildRenderTree(builder);
    }

    private sealed class DriverPage : LinkEts2Nav.Pages.Navigation
    {
        public RenderTreeFrame[] Attributes()
        {
            using var builder = new RenderTreeBuilder();
            BuildRenderTree(builder);
            var frames = builder.GetFrames();
            return frames.Array.Take(frames.Count).Where(f => f.FrameType == RenderTreeFrameType.Attribute).ToArray();
        }
    }

    [Fact]
    public async Task SettingsUseTheSystemBrowserAndLeaveACopyableLinkIfItCannotOpen()
    {
        PlayerSettingsSession.Initialize();
        await using var page = new DriverPage();
        var requests = new List<ProcessStartInfo>();
        page.StartBrowser = info =>
        {
            requests.Add(info);
            if (requests.Count == 1) throw new Win32Exception(1155);
            return null; // An existing browser can accept the URL without returning a new process.
        };
        try
        {
            await page.OpenSettings();
            var first = Assert.Single(requests);
            Assert.True(first.UseShellExecute);
            var address = new Uri(first.FileName);
            Assert.Equal("http", address.Scheme);
            Assert.True(address.IsLoopback);
            Assert.NotEmpty(address.Fragment);
            var failed = page.Attributes();
            Assert.Contains(failed, f => f.AttributeName == "value" && Equals(f.AttributeValue, first.FileName));
            Assert.Contains(failed, f => f.AttributeName == "readonly");
            Assert.DoesNotContain(failed, f => f.AttributeName == "href" && Equals(f.AttributeValue, first.FileName));

            await page.OpenSettings();
            Assert.Equal(2, requests.Count);
            Assert.Equal(first.FileName, requests[1].FileName);
            Assert.True(requests[1].UseShellExecute);
            Assert.DoesNotContain(page.Attributes(), f => Equals(f.AttributeValue, first.FileName));
            using var browser = new HttpClient(new HttpClientHandler { UseProxy = false });
            var html = await browser.GetStringAsync(address, TestContext.Current.CancellationToken);
            Assert.Contains("导航设置", html);
        }
        finally
        {
            await PlayerSettingsSession.CloseAsync();
        }
    }
    [Fact]
    public async Task DeveloperAudioAndStatisticsControlsCompileToInteractiveBlazorBindings()
    {
        await using var page=new Page();
        using var builder=new RenderTreeBuilder();
        page.Render(builder);
        var frames=builder.GetFrames();
        var attributes=frames.Array.Take(frames.Count).Where(f=>f.FrameType==RenderTreeFrameType.Attribute).ToArray();
        Assert.DoesNotContain(attributes,a=>a.AttributeName.StartsWith("@",StringComparison.Ordinal));
        var buttons=attributes.Where(a=>a.AttributeName=="onclick").ToArray();
        Assert.Equal(3,buttons.Length);
        Assert.All(buttons,b=>Assert.True(b.AttributeValue is MulticastDelegate or EventCallback<MouseEventArgs>));
        Assert.Contains(attributes,a=>a.AttributeName=="onchange" && a.AttributeValue is MulticastDelegate or EventCallback or EventCallback<ChangeEventArgs>);
    }
}
#pragma warning restore BL0006
