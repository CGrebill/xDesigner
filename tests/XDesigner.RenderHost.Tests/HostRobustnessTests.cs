using Avalonia;
using Avalonia.Threading;
using XDesigner.RenderHost.Rendering;

namespace XDesigner.RenderHost.Tests;

/// <summary>Renderer cases from the 1.0.0 code review (R19, R20, L-R3).</summary>
public class HostRobustnessTests(RenderHostFixture host)
{
    [Fact]
    public async Task AnExceptionFromUserCodeOutsideARender_DoesNotStopTheHost()
    {
        // As from a user control's timer tick or async void handler: not inside any render or RPC call.
        await host.Runtime.InvokeAsync(() =>
        {
            Dispatcher.UIThread.Post(() => throw new InvalidOperationException("boom from a timer"));
            return 0;
        });
        await Task.Delay(200, TestContext.Current.CancellationToken);

        var result = await host.RenderAsync($"<UserControl {RenderXaml.Namespaces}><TextBlock Text=\"still here\" /></UserControl>");

        Assert.True(result.Success, result.Error?.Message);
    }

    [Theory]
    [InlineData(800, 450, 2.0, 2.0)]        // well under the cap: as requested
    [InlineData(1920, 1080, 6.0, 0)]        // 298 MP requested: lowered (0 = check the cap only)
    [InlineData(100, 100, double.NaN, 1.0)] // nonsense scale: 1
    public void LargeFrames_AreRenderedAtALowerScale(double width, double height, double requested, double expected)
    {
        var scale = XamlRenderer.EffectiveScale(new Size(width, height), requested);

        if (expected > 0)
        {
            Assert.Equal(expected, scale);
        }
        else
        {
            Assert.True(scale < requested);
            Assert.True(width * height * scale * scale <= XamlRenderer.MaxFramePixels * 1.0001);
        }
    }

    [Fact]
    public async Task ErrorLines_CountTheLinesBeforeTheRoot()
    {
        const string text = "<!-- license, line 1 -->\n<!-- line 2 -->\n<UserControl {0}>\n  <NoSuchControl />\n</UserControl>";

        var result = await host.RenderAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, text, RenderXaml.Namespaces));

        Assert.False(result.Success);
        Assert.Equal(4, result.Error!.Line);
    }
}
