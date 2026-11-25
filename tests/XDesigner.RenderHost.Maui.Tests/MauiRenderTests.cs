using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;
using XDesigner.Protocol.Rendering;

namespace XDesigner.RenderHost.Maui.Tests;

/// <summary>Rendering MAUI documents and reading their metadata over the pipe, on MAUI 10 and 11 (§14.6).</summary>
public class MauiRenderTests(MauiHostsFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public async Task Handshake_ReportsMauiAndTheSampleApp(int major)
    {
        var info = (await fixture.HostAsync(major)).Info;

        Assert.Equal($"Maui{major}", info.FrameworkId);
        Assert.StartsWith($"{major}.", info.FrameworkVersion);
        Assert.Equal("SampleMauiApp.App", info.ApplicationType);
        Assert.Equal("MauiProgram", info.BuilderSource);
        Assert.Empty(info.Warnings);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public async Task SampleMainPage_RendersAsWritten(int major)
    {
        var host = await fixture.HostAsync(major);
        // The real file: x:Class and Clicked="button1_Clicked" are handled by the host.
        var source = await File.ReadAllTextAsync(Path.Combine(TestPaths.SampleDirectory, "MainPage.xaml"), Ct);

        var result = await host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(source), 1.5, DesignSize: new SizeDto(480, 640), DocumentId: "main"), Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal((720, 960), (result.PixelWidth, result.PixelHeight));
        var all = RenderXaml.All(result.Root!).ToList();
        Assert.Contains(all, e => e.TypeName == "SampleMauiApp.Controls.ContactCard");
        Assert.Contains(all, e => e.TypeName == "SampleMauiApp.Controls.StarRating");
        Assert.Equal(PanelKind.Canvas, all.Single(e => e.TypeName == "Microsoft.Maui.Controls.AbsoluteLayout").Panel!.Kind);
        var grid = all.Single(e => e.TypeName == "Microsoft.Maui.Controls.Grid");
        Assert.Equal(PanelKind.Grid, grid.Panel!.Kind);
        Assert.Equal(4, grid.Panel.RowOffsets!.Length);
        Assert.Equal(16, grid.Panel.RowOffsets[0]);

        // The project's image (a MauiImage, resolved through the project's resource index) and the App's color.
        var image = all.Single(e => e.TypeName == "Microsoft.Maui.Controls.Image").Bounds;
        var red = from y in Enumerable.Range(0, (int)image.Height)
                  from x in Enumerable.Range(0, (int)image.Width)
                  where Rgb(RenderXaml.Pixel(result, image.X + x, image.Y + y)) == (0, 0, 255)
                  select (x, y);
        Assert.NotEmpty(red); // the 8×8 picture, drawn somewhere in its 32×32 box
        var card = all.Single(e => e.TypeName == "SampleMauiApp.Controls.ContactCard").Bounds;
        // The App's #0078D4 on the stroke. Anti-aliasing blends its edge pixels with what's either side, differently per
        // renderer (GitHub's runners draw in software), so look across the stroke for a pixel close to it.
        var across = Enumerable.Range(0, 4).Select(dx => Rgb(RenderXaml.Pixel(result, card.X + dx, card.Y + (card.Height / 2)))).ToList();
        Assert.True(
            across.Any(p => Math.Abs(p.B - 0xD4) <= 40 && Math.Abs(p.G - 0x78) <= 30 && p.R <= 40),
            $"No pixel near #0078D4 across the card's stroke: {string.Join(", ", across.Select(p => $"rgb({p.R},{p.G},{p.B})"))}");
    }

    [Fact]
    public async Task Maui11File_WithoutNamespaces_Renders()
    {
        var host = await fixture.HostAsync(11);
        var xaml = """
            <ContentView x:Class="App.Card" WidthRequest="200" HeightRequest="100">
              <Label x:Name="label" Text="No namespaces" />
            </ContentView>
            """;

        var result = await host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml)), Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal((200, 100), (result.PixelWidth, result.PixelHeight));
        Assert.Equal("Microsoft.Maui.Controls.Label", Assert.Single(result.Root!.Children).TypeName);
    }

    /// <summary>Non-visual elements MAUI resolves by suffix or arity (OnPlatform`1, StaticResourceExtension) render (review H4).</summary>
    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public async Task MarkupExtensionAndGenericElements_Render(int major)
    {
        var host = await fixture.HostAsync(major);
        var xaml = """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui" xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
                         WidthRequest="200" HeightRequest="100">
              <ContentView.Resources>
                <Color x:Key="c">Red</Color>
              </ContentView.Resources>
              <ContentView.Padding>
                <OnPlatform x:TypeArguments="Thickness" Default="4">
                  <On Platform="WinUI" Value="8" />
                </OnPlatform>
              </ContentView.Padding>
              <Label x:Name="label" Text="Hi">
                <Label.TextColor>
                  <StaticResource Key="c" />
                </Label.TextColor>
                <Label.BackgroundColor>
                  <DynamicResource Key="c" />
                </Label.BackgroundColor>
              </Label>
            </ContentView>
            """;

        var result = await host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml)), Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("Microsoft.Maui.Controls.Label", Assert.Single(result.Root!.Children).TypeName);
    }

    /// <summary>A hidden child takes no room in an Auto row (review L41); a style's value isn't reported as Local (review L42).</summary>
    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public async Task HiddenAutoRowChild_AndStyleValues(int major)
    {
        var host = await fixture.HostAsync(major);
        var xaml = """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui" xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
                         WidthRequest="200" HeightRequest="100">
              <ContentView.Resources>
                <Style TargetType="Label">
                  <Setter Property="FontSize" Value="20" />
                </Style>
              </ContentView.Resources>
              <Grid RowDefinitions="Auto,*">
                <Label Text="Hidden" IsVisible="False" Margin="0,10" />
                <Label Grid.Row="1" Text="Shown" TextColor="Red" />
              </Grid>
            </ContentView>
            """;

        var result = await host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml), DocumentId: "auto-style"), Ct);

        Assert.True(result.Success, result.Error?.Message);
        var grid = RenderXaml.All(result.Root!).Single(e => e.TypeName == "Microsoft.Maui.Controls.Grid");
        Assert.Equal(0, grid.Panel!.RowOffsets![1], precision: 1); // the Auto row is empty
        var values = (await host.Host.GetValuesAsync("auto-style", 6, Ct)).ToDictionary(v => v.Key); // the shown Label (ids count the Style and Setter too)
        Assert.Equal(XDesigner.Protocol.Metadata.ValueSource.Style, values["FontSize"].Source);
        Assert.Equal(XDesigner.Protocol.Metadata.ValueSource.Local, values["TextColor"].Source);
    }

    /// <summary>A Window document draws its page and reports the Window around it, as the WinUI renderer does.</summary>
    [Fact]
    public async Task WindowRoot_DrawsItsPage()
    {
        var host = await fixture.HostAsync(11);
        var xaml = """
            <Window xmlns="http://schemas.microsoft.com/dotnet/2021/maui" xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml" Title="Settings">
              <ContentPage>
                <Label Text="Inside" />
              </ContentPage>
            </Window>
            """;

        var result = await host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml), DesignSize: new SizeDto(300, 200)), Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(RootKind.Window, result.RootKind);
        Assert.Equal("Settings", result.WindowTitle);
        Assert.Equal(1, result.Root!.DesignId);
        Assert.Contains(RenderXaml.All(result.Root), e => e.TypeName == "Microsoft.Maui.Controls.Label");
    }

    /// <summary>MAUI's global namespace (MAUI 10+) is understood (review M15).</summary>
    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public async Task GlobalNamespace_Renders(int major)
    {
        var host = await fixture.HostAsync(major);
        var xaml = """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/maui/global" xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
                         WidthRequest="200" HeightRequest="100">
              <Label Text="Global" />
            </ContentView>
            """;

        var result = await host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml)), Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("Microsoft.Maui.Controls.Label", Assert.Single(result.Root!.Children).TypeName);
    }

    [Fact]
    public async Task XamlError_ReportsLineAndColumn()
    {
        var host = await fixture.HostAsync(11);
        var xaml = $"""
            <ContentView {RenderXaml.Namespaces}>
              <VerticalStackLayout>
                <NoSuchView />
              </VerticalStackLayout>
            </ContentView>
            """;

        var result = await host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml)), Ct);

        Assert.False(result.Success);
        Assert.Equal(3, result.Error!.Line);
        Assert.DoesNotContain("Position", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public async Task Metadata_DescribesMauiAndProjectTypes(int major)
    {
        var host = await fixture.HostAsync(major);
        var types = (await host.Host.GetControlTypesAsync(Ct)).ToDictionary(t => t.FullName);

        var button = types["Microsoft.Maui.Controls.Button"];
        Assert.True(button.IsBuiltIn);
        Assert.Equal("http://schemas.microsoft.com/dotnet/2021/maui", button.XmlNamespace);
        Assert.Equal("Clicked", button.DefaultEventName);
        Assert.Equal(ContentModel.Children, types["Microsoft.Maui.Controls.Grid"].Content);
        Assert.Equal(ContentModel.SingleContent, types["Microsoft.Maui.Controls.ScrollView"].Content);
        Assert.Equal(ContentModel.SingleContent, types["Microsoft.Maui.Controls.Border"].Content);
        Assert.Equal(ContentModel.None, types["Microsoft.Maui.Controls.Label"].Content);
        var card = types["SampleMauiApp.Controls.ContactCard"];
        Assert.Equal("clr-namespace:SampleMauiApp.Controls;assembly=SampleMauiApp", card.XmlNamespace);
        Assert.Equal(ContentModel.None, card.Content); // the project's own ContentView

        var properties = (await host.Host.GetPropertiesAsync("Microsoft.Maui.Controls.Label", Ct)).ToDictionary(p => p.Key);
        Assert.Equal(ValueKind.Double, properties["WidthRequest"].Kind);
        Assert.Equal(ValueKind.Enum, properties["HorizontalOptions"].Kind);
        Assert.Equal(["Start", "Center", "End", "Fill"], properties["HorizontalOptions"].EnumValues!);
        Assert.Equal(ValueKind.Color, properties["TextColor"].Kind);
        Assert.Equal(ValueKind.FontFamily, properties["FontFamily"].Kind);
        Assert.Equal(ValueKind.Int, properties["Grid.Row"].Kind);
        Assert.True(properties["AbsoluteLayout.LayoutBounds"].IsAttached);

        var events = (await host.Host.GetEventsAsync("Microsoft.Maui.Controls.Button", Ct)).ToDictionary(e => e.Name);
        Assert.Equal(("object?", "EventArgs"), (events["Clicked"].SenderType, events["Clicked"].ArgsType));
    }

    [Fact]
    public async Task Values_ReportLocalAndDefaultValues()
    {
        var host = await fixture.HostAsync(11);
        var xaml = $"""
            <ContentView {RenderXaml.Namespaces} WidthRequest="300" HeightRequest="100">
              <local:StarRating Value="4" Margin="8,4" HorizontalOptions="Center" />
            </ContentView>
            """;
        var render = await host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml), DocumentId: "values"), Ct);
        Assert.True(render.Success, render.Error?.Message);

        var values = (await host.Host.GetValuesAsync("values", 2, Ct)).ToDictionary(v => v.Key);

        Assert.Equal(("4", ValueSource.Local), (values["Value"].Value, values["Value"].Source));
        Assert.Equal(("5", ValueSource.Default), (values["Maximum"].Value, values["Maximum"].Source));
        Assert.Equal("8,4", values["Margin"].Value);
        Assert.Equal("Center", values["HorizontalOptions"].Value);
    }

    private static (byte B, byte G, byte R) Rgb((byte B, byte G, byte R, byte A) p) => (p.B, p.G, p.R);
}
