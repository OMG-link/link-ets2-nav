using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
namespace LinkEts2Nav.UnitTests;

// Inspect generated event bindings: static HTML rendering cannot distinguish a literal @onclick.
#pragma warning disable BL0006
public sealed class DiagnosticsInteractionTests
{
    private sealed class Page : LinkEts2Nav.Pages.Diagnostics
    {
        public void Render(RenderTreeBuilder builder) => BuildRenderTree(builder);
    }
    [Fact]
    public async Task ButtonsAndCommentInputCompileToInteractiveBlazorBindings()
    {
        await using var page=new Page();
        using var builder=new RenderTreeBuilder();
        page.Render(builder);
        var frames=builder.GetFrames();
        var attributes=frames.Array.Take(frames.Count).Where(f=>f.FrameType==RenderTreeFrameType.Attribute).ToArray();
        Assert.DoesNotContain(attributes,a=>a.AttributeName.StartsWith("@",StringComparison.Ordinal));
        var buttons=attributes.Where(a=>a.AttributeName=="onclick").ToArray();
        Assert.Equal(2,buttons.Length);
        Assert.All(buttons,b=>Assert.True(b.AttributeValue is MulticastDelegate or EventCallback<MouseEventArgs>));
        Assert.Contains(attributes,a=>a.AttributeName=="oninput" && a.AttributeValue is MulticastDelegate or EventCallback or EventCallback<ChangeEventArgs>);
    }
}
#pragma warning restore BL0006
