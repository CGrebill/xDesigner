using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using XDesigner.Protocol.Rendering;

namespace XDesigner.RenderHost.Tests;

/// <summary>
/// The Avalonia 12 behaviour the renderer relies on (docs/ARCHITECTURE.md §14).
/// </summary>
public class XamlRendererTests(RenderHostFixture host, ITestOutputHelper output)
{
    [Fact]
    public void Setup_UsesTheUserProjectsAppInDesignMode()
    {
        Assert.Equal("SampleAvaloniaApp.App", host.Runtime.ApplicationTypeName);
        Assert.Equal("BuildAvaloniaApp", host.Runtime.BuilderSource);
        Assert.True(host.Runtime.IsDesignMode);
        Assert.True(Design.IsDesignMode);
        Assert.Empty(host.Runtime.Warnings);
    }

    [Fact]
    public async Task Window_RendersAtRequestedScaleWithEveryDesignId()
    {
        var result = await host.RenderAsync(RenderXaml.SampleMainWindow, scale: 1.5);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(RootKind.Window, result.RootKind);
        Assert.Equal("Sample Avalonia App", result.WindowTitle);
        Assert.Equal((960, 630), (result.PixelWidth, result.PixelHeight));
        Assert.Equal(960 * 630 * 4, result.Bgra!.Length);

        var all = Flatten(result.Root!).ToDictionary(e => e.DesignId);
        Assert.Equal(Enumerable.Range(1, 7), all.Keys.Order());
        Assert.Equal(new RectDto(0, 0, 640, 420), all[1].Bounds);
        Assert.Equal(new RectDto(16, 16, 608, 388), all[2].Bounds);
        Assert.Equal(new ThicknessDto(16, 16, 16, 16), all[2].Margin);
        Assert.Equal("SampleAvaloniaApp.Controls.ContactCard", all[5].TypeName);
        Assert.Equal("SampleAvaloniaApp.Controls.StarRating", all[6].TypeName);

        // Parent/child structure follows the XAML, skipping template parts in between.
        Assert.Equal([2], all[1].Children.Select(c => c.DesignId));
        Assert.Equal([3, 4, 7], all[2].Children.Select(c => c.DesignId));
        Assert.Equal([5, 6], all[4].Children.Select(c => c.DesignId));
    }

    [Fact]
    public async Task Grid_ReportsRowAndColumnOffsets()
    {
        var result = await host.RenderAsync(RenderXaml.SampleMainWindow);

        var grid = Flatten(result.Root!).Single(e => e.DesignId == 2);
        Assert.Equal(PanelKind.Grid, grid.Panel!.Kind);
        Assert.Equal(4, grid.Panel.RowOffsets!.Length); // 3 rows → 4 edges
        Assert.Equal(0, grid.Panel.RowOffsets[0]);
        Assert.Equal(grid.Bounds.Height, grid.Panel.RowOffsets[^1], precision: 3);
        Assert.Equal([0, grid.Bounds.Width], grid.Panel.ColumnOffsets!); // no column definitions → one implicit column

        var stack = Flatten(result.Root!).Single(e => e.DesignId == 4);
        Assert.Equal(new PanelInfo(PanelKind.StackPanel, LayoutOrientation.Vertical), stack.Panel);
    }

    [Fact]
    public async Task CustomDrawnControl_FromUserAssembly_Paints()
    {
        var result = await host.RenderAsync(RenderXaml.SampleMainWindow);

        // Centre of the first (filled, Goldenrod #DAA520) star.
        var star = Flatten(result.Root!).Single(e => e.DesignId == 6).Bounds;
        var (b, g, r, _) = Pixel(result, star.X + 12, star.Y + 12);
        Assert.Equal((0xDA, 0xA5, 0x20), (r, g, b));
    }

    [Fact]
    public async Task UserAppStyles_AreApplied()
    {
        var result = await host.RenderAsync(RenderXaml.SampleMainWindow);

        // FluentTheme gives a Button a grey background; unstyled it would be transparent (white window).
        var button = Flatten(result.Root!).Single(e => e.DesignId == 7).Bounds;
        var (b, g, r, _) = Pixel(result, button.X + 4, button.Y + button.Height / 2);
        Assert.True(r < 240 && r == g && g == b, $"Expected a grey Fluent button background, got rgb({r},{g},{b}).");
    }

    [Fact]
    public async Task UserControl_UsesDesignSize()
    {
        var xaml = $"""
            <UserControl {RenderXaml.Namespaces} d:DesignWidth="320" d:DesignHeight="90" dsn:DesignId.Value="1">
              <Border Padding="12" dsn:DesignId.Value="2"><TextBlock Text="Card" dsn:DesignId.Value="3" /></Border>
            </UserControl>
            """;

        var result = await host.RenderAsync(xaml);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(RootKind.UserControl, result.RootKind);
        Assert.Equal((320, 90), (result.PixelWidth, result.PixelHeight));
        Assert.Null(result.WindowTitle);
        Assert.Equal(new ThicknessDto(12, 12, 12, 12), Flatten(result.Root!).Single(e => e.DesignId == 2).Padding);
    }

    [Fact]
    public async Task UserControl_WithoutDesignSize_UsesDefault_AndRequestSizeOverrides()
    {
        var xaml = $"""<UserControl {RenderXaml.Namespaces} dsn:DesignId.Value="1"><Grid dsn:DesignId.Value="2" /></UserControl>""";

        var byDefault = await host.RenderAsync(xaml);
        var overridden = await host.RenderAsync(xaml, size: new SizeDto(400, 700));

        Assert.Equal((800, 450), (byDefault.PixelWidth, byDefault.PixelHeight));
        Assert.Equal((400, 700), (overridden.PixelWidth, overridden.PixelHeight));
    }

    [Fact]
    public async Task DarkTheme_ChangesTheBackground()
    {
        var light = await host.RenderAsync(RenderXaml.SampleMainWindow, theme: ThemeVariantKind.Light);
        var dark = await host.RenderAsync(RenderXaml.SampleMainWindow, theme: ThemeVariantKind.Dark);

        Assert.True(Pixel(light, 300, 300).R > 200, "Light theme background should be light.");
        Assert.True(Pixel(dark, 300, 300).R < 80, "Dark theme background should be dark.");
    }

    [Fact]
    public async Task MalformedXaml_ReportsPosition()
    {
        var result = await host.RenderAsync($"<UserControl {RenderXaml.Namespaces}>\n  <Grid>\n</UserControl>");

        Assert.False(result.Success);
        Assert.NotNull(result.Error!.Line);
        output.WriteLine($"{result.Error.ExceptionType}: {result.Error.Message} (line {result.Error.Line}, col {result.Error.Column})");
    }

    [Fact]
    public async Task UnknownType_ReportsError()
    {
        var result = await host.RenderAsync($"""<UserControl {RenderXaml.Namespaces}><NoSuchControl /></UserControl>""");

        Assert.False(result.Success);
        Assert.Contains("NoSuchControl", result.Error!.Message);
        output.WriteLine($"{result.Error.ExceptionType}: {result.Error.Message} (line {result.Error.Line})");
    }

    [Fact]
    public async Task ThrowingUserControl_IsReportedWithItsType()
    {
        var result = await host.RenderAsync($"""<UserControl {RenderXaml.Namespaces}><t:ThrowingControl /></UserControl>""");

        Assert.False(result.Success);
        output.WriteLine($"{result.Error!.ExceptionType}: {result.Error.Message}");
        Assert.Contains("boom at design time", result.Error!.Message);
        Assert.Equal(typeof(ThrowingControl).FullName, result.Error.OffendingType);
    }

    [Fact]
    public async Task NonControlRoot_IsRejected()
    {
        var result = await host.RenderAsync($"""<ResourceDictionary {RenderXaml.Namespaces} />""");

        Assert.False(result.Success);
        Assert.Contains("ResourceDictionary", result.Error!.Message);
    }

    [Fact]
    public async Task RepeatedRenders_AreFastEnoughForInteractiveEditing()
    {
        await host.RenderAsync(RenderXaml.SampleMainWindow); // warm-up

        var times = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            var result = await host.RenderAsync(RenderXaml.SampleMainWindow, scale: 1.5);
            times.Add(sw.Elapsed.TotalMilliseconds);
            Assert.True(result.Success);
        }

        output.WriteLine($"Render times (ms): {string.Join(", ", times.Select(t => t.ToString("F0", CultureInfo.InvariantCulture)))}");
        // Generous bound; the real target (~100 ms, §8.2) is tracked from the logged numbers.
        Assert.True(times.Min() < 1000, $"Fastest render took {times.Min():F0} ms.");
    }

    private static IEnumerable<ElementLayout> Flatten(ElementLayout root) =>
        root.Children.SelectMany(Flatten).Prepend(root);

    /// <summary>BGRA at a DIP position.</summary>
    private static (byte B, byte G, byte R, byte A) Pixel(RenderResult result, double x, double y)
    {
        var px = (int)(x * result.Scale);
        var py = (int)(y * result.Scale);
        var i = (py * result.PixelWidth + px) * 4;
        return (result.Bgra![i], result.Bgra[i + 1], result.Bgra[i + 2], result.Bgra[i + 3]);
    }
}

/// <summary>A user control that fails at design time, like a control that needs a missing service.</summary>
public class ThrowingControl : Control
{
    public ThrowingControl() => throw new InvalidOperationException("boom at design time");
}
