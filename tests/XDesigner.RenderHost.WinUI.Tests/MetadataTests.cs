using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;
using XDesigner.Protocol.Rendering;

namespace XDesigner.RenderHost.WinUI.Tests;

/// <summary>Control types, properties, values and events reported by the WinUI host (§14.3, §14.4).</summary>
public class MetadataTests(WinUIHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ControlTypes_IncludeWinUIAndTheProjectsOwn()
    {
        var types = (await fixture.Host.Host.GetControlTypesAsync(Ct)).ToDictionary(t => t.FullName);

        var button = types["Microsoft.UI.Xaml.Controls.Button"];
        Assert.True(button.IsBuiltIn);
        Assert.True(button.IsCreatable);
        Assert.Equal("http://schemas.microsoft.com/winfx/2006/xaml/presentation", button.XmlNamespace);
        Assert.Equal(ContentModel.SingleContent, button.Content);
        Assert.Equal("Click", button.DefaultEventName);

        var stars = types["SampleWinUIApp.Controls.StarRating"];
        Assert.False(stars.IsBuiltIn);
        Assert.Equal("using:SampleWinUIApp.Controls", stars.XmlNamespace);
        Assert.Equal(ContentModel.None, stars.Content); // a project's own UserControl

        Assert.Equal(ContentModel.Children, types["Microsoft.UI.Xaml.Controls.StackPanel"].Content);
        Assert.Equal(ContentModel.SingleChild, types["Microsoft.UI.Xaml.Controls.Border"].Content);
        Assert.Equal(ContentModel.Inlines, types["Microsoft.UI.Xaml.Controls.TextBlock"].Content);
        Assert.Equal(ContentModel.Items, types["Microsoft.UI.Xaml.Controls.ListView"].Content);
        Assert.Equal(ContentModel.Items, types["Microsoft.UI.Xaml.Controls.TabView"].Content);
        Assert.Equal("TabItems", types["Microsoft.UI.Xaml.Controls.TabView"].ContentPropertyName);
        Assert.Contains("Microsoft.UI.Xaml.Window", types.Keys);
        Assert.Contains("Microsoft.UI.Xaml.Controls.NumberBox", types.Keys);
    }

    [Fact]
    public async Task Properties_IncludeDependencyAndAttachedProperties()
    {
        var properties = (await fixture.Host.Host.GetPropertiesAsync("Microsoft.UI.Xaml.Controls.Button", Ct)).ToDictionary(p => p.Key);

        Assert.Equal(ValueKind.Brush, properties["Background"].Kind);
        Assert.Equal(ValueKind.Thickness, properties["Margin"].Kind);
        Assert.Equal(ValueKind.Object, properties["Content"].Kind);
        Assert.Equal(ValueKind.Enum, properties["HorizontalAlignment"].Kind);
        Assert.Equal(ValueKind.Int, properties["Grid.Row"].Kind);
        Assert.True(properties["Grid.Row"].IsAttached);
        Assert.Equal(ValueKind.Double, properties["Canvas.Left"].Kind);
        Assert.Equal(ValueKind.Object, properties["ToolTipService.ToolTip"].Kind);

        var stars = (await fixture.Host.Host.GetPropertiesAsync("SampleWinUIApp.Controls.StarRating", Ct)).ToDictionary(p => p.Key);
        Assert.Equal(ValueKind.Int, stars["Value"].Kind);
    }

    [Fact]
    public async Task Values_ReportLocalAndDefaultValues()
    {
        var xaml = $"""
            <StackPanel {RenderXaml.Namespaces} Width="300" Height="100">
              <local:StarRating Value="4" Margin="8,4" />
            </StackPanel>
            """;
        var render = await fixture.Host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml), DocumentId: "values"), Ct);
        Assert.True(render.Success, render.Error?.Message);

        var values = (await fixture.Host.Host.GetValuesAsync("values", 2, Ct)).ToDictionary(v => v.Key);

        Assert.Equal(("4", ValueSource.Local), (values["Value"].Value, values["Value"].Source));
        Assert.Equal(("5", ValueSource.Default), (values["Maximum"].Value, values["Maximum"].Source));
        Assert.Equal("8,4", values["Margin"].Value);
        Assert.Equal("NaN", values["Width"].Value);
    }

    [Fact]
    public async Task Events_HaveHandlerSignatures()
    {
        var button = (await fixture.Host.Host.GetEventsAsync("Microsoft.UI.Xaml.Controls.Button", Ct)).ToDictionary(e => e.Name);
        Assert.Equal(("object", "RoutedEventArgs", "Microsoft.UI.Xaml"), (button["Click"].SenderType, button["Click"].ArgsType, button["Click"].ArgsNamespace));
        Assert.True(button["PointerPressed"].IsRouted);

        var numberBox = (await fixture.Host.Host.GetEventsAsync("Microsoft.UI.Xaml.Controls.NumberBox", Ct)).ToDictionary(e => e.Name);
        Assert.Equal("Microsoft.UI.Xaml.Controls.NumberBox", numberBox["ValueChanged"].SenderType);
        Assert.Equal("NumberBoxValueChangedEventArgs", numberBox["ValueChanged"].ArgsType);
    }

    [Fact]
    public async Task Fonts_IncludeTheSystemUIFont()
    {
        var fonts = await fixture.Host.Host.GetFontFamiliesAsync(Ct);

        Assert.Contains("Segoe UI", fonts);
        Assert.DoesNotContain(fonts, f => f.Contains("TrueType", StringComparison.Ordinal));
    }
}
