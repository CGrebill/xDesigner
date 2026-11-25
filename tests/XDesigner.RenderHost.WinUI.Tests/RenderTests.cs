using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;
using XDesigner.Protocol.Rendering;

namespace XDesigner.RenderHost.WinUI.Tests;

/// <summary>Rendering WinUI documents over the pipe (§14.5).</summary>
public class RenderTests(WinUIHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Handshake_ReportsWinUIAndTheSampleProject()
    {
        var info = fixture.Host.Info;

        Assert.Equal("WinUI3", info.FrameworkId);
        Assert.StartsWith("2.", info.FrameworkVersion);
        Assert.Equal("SampleWinUIApp", info.UserAssemblyName);
        Assert.Equal("App.xaml resources", info.BuilderSource);
        Assert.Empty(info.Warnings);
    }

    [Fact]
    public async Task SampleMainWindow_RendersAsWritten()
    {
        // The real file: x:Class, Click="button1_Click", d: and the Window itself are handled by the host.
        var source = await File.ReadAllTextAsync(Path.Combine(TestPaths.SampleDirectory, "MainWindow.xaml"), Ct);

        var result = await fixture.Host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(source), 1.5, DocumentId: "main"), Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(RootKind.Window, result.RootKind);
        Assert.Equal("Sample WinUI App", result.WindowTitle);
        Assert.Equal((960, 630), (result.PixelWidth, result.PixelHeight)); // d:DesignWidth/Height 640×420 at 1.5
        Assert.Equal("Microsoft.UI.Xaml.Window", result.Root!.TypeName);
        Assert.Null(result.Root.Panel);
        var types = RenderXaml.All(result.Root).Select(e => e.TypeName).ToList();
        Assert.Contains("SampleWinUIApp.Controls.ContactCard", types);
        Assert.Contains("SampleWinUIApp.Controls.StarRating", types);
    }

    [Fact]
    public async Task ProjectImage_LoadsFromTheBuildOutput()
    {
        var xaml = $"""
            <UserControl {RenderXaml.Namespaces} Width="40" Height="40">
              <Image Source="ms-appx:///Assets/red.png" Stretch="Fill" />
            </UserControl>
            """;

        var result = await fixture.Host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml)), Ct);

        Assert.True(result.Success, result.Error?.Message);
        var (b, g, r, _) = RenderXaml.Pixel(result, 20, 20);
        Assert.Equal((0, 0, 255), (b, g, r));
    }

    /// <summary>x:Uid strings and scale-qualified images resolve as in the app (review M27).</summary>
    [Fact]
    public async Task ProjectStringsAndScaledImages_Resolve()
    {
        var xaml = $"""
            <StackPanel {RenderXaml.Namespaces} Width="200" Height="80">
              <TextBlock x:Uid="Greeting" />
              <Image Source="ms-appx:///Assets/badge.png" Width="40" Height="40" Stretch="Fill" HorizontalAlignment="Left" />
            </StackPanel>
            """;

        var result = await fixture.Host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml), DocumentId: "strings"), Ct);

        Assert.True(result.Success, result.Error?.Message);
        var values = (await fixture.Host.Host.GetValuesAsync("strings", 2, Ct)).ToDictionary(v => v.Key);
        Assert.Equal("Hello from the strings", values["Text"].Value);
        var image = RenderXaml.All(result.Root!).Single(e => e.TypeName.EndsWith(".Image", StringComparison.Ordinal)).Bounds;
        var (b, g, r, _) = RenderXaml.Pixel(result, image.X + 20, image.Y + 20);
        Assert.Equal((0, 0, 255), (b, g, r)); // badge.scale-200.png, the only file there is
    }

    /// <summary>A root keeps its own MaxWidth and Margin instead of being stretched to the design size (review M22).</summary>
    [Fact]
    public async Task Root_KeepsItsOwnSizeLimitsAndMargin()
    {
        var xaml = $"""
            <UserControl {RenderXaml.Namespaces} MaxWidth="100" Margin="10" HorizontalAlignment="Left" VerticalAlignment="Top" Height="50">
              <Border Width="150" />
            </UserControl>
            """;

        var result = await fixture.Host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml), DesignSize: new SizeDto(300, 100), DocumentId: "maxwidth"), Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal((300, 100), (result.PixelWidth, result.PixelHeight));
        Assert.Equal(new RectDto(10, 10, 100, 50), result.Root!.Bounds);
        var values = (await fixture.Host.Host.GetValuesAsync("maxwidth", 1, Ct)).ToDictionary(v => v.Key);
        Assert.NotEqual(ValueSource.Local, values["Width"].Source);
    }

    /// <summary>A Window written with &lt;Window.Content&gt; shows its content, and its title is a value (review L39).</summary>
    [Fact]
    public async Task WindowContentPropertyElement_Renders()
    {
        var xaml = $"""
            <Window {RenderXaml.Namespaces} Title="Settings">
              <Window.Content>
                <Button Content="OK" />
              </Window.Content>
            </Window>
            """;

        var result = await fixture.Host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml), DocumentId: "window-content"), Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("Settings", result.WindowTitle);
        Assert.Contains(RenderXaml.All(result.Root!), e => e.TypeName.EndsWith(".Button", StringComparison.Ordinal));
        var values = (await fixture.Host.Host.GetValuesAsync("window-content", 1, Ct)).ToDictionary(v => v.Key);
        Assert.Equal("Settings", values["Title"].Value);
    }

    /// <summary>A render that arrives after its document closed draws nothing and keeps nothing alive (review L40).</summary>
    [Fact]
    public async Task RenderAfterClose_IsRefused()
    {
        var xaml = $"""<UserControl {RenderXaml.Namespaces} Width="40" Height="40" />""";
        await fixture.Host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml), DocumentId: "closed"), Ct);
        await fixture.Host.Host.CloseDocumentAsync("closed", Ct);

        var late = await fixture.Host.Host.RenderAsync(new RenderRequest(2, RenderCopyBuilder.Build(xaml), DocumentId: "closed"), Ct);

        Assert.False(late.Success);
    }

    [Fact]
    public async Task UserControl_UsesTheAppsResources()
    {
        // ContactCard's border is {StaticResource CardBorderBrush} from the sample's App.xaml (#0078D4).
        var xaml = $"""
            <UserControl {RenderXaml.Namespaces} Width="320" Height="90">
              <local:ContactCard />
            </UserControl>
            """;

        var result = await fixture.Host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml)), Ct);

        Assert.True(result.Success, result.Error?.Message);
        var (b, g, r, _) = RenderXaml.Pixel(result, 1, 45);
        Assert.Equal((0xD4, 0x78, 0x00), (b, g, r));
    }

    [Fact]
    public async Task Page_IsReportedWithoutWindowChrome()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(TestPaths.SampleDirectory, "Pages", "SettingsPage.xaml"), Ct);

        var result = await fixture.Host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(source)), Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(RootKind.UserControl, result.RootKind);
        Assert.Equal((480, 300), (result.PixelWidth, result.PixelHeight));
    }

    [Fact]
    public async Task CompiledBindingsAndHandlers_AreRemovedBeforeLoading()
    {
        var xaml = $$"""
            <UserControl {{RenderXaml.Namespaces}} x:Class="SampleWinUIApp.Controls.ContactCard" Loaded="OnLoaded">
              <StackPanel x:Load="True">
                <TextBlock Text="{x:Bind ViewModel.Name, Mode=OneWay}" x:Phase="1" />
                <Button Click="handlerTheDesignerJustCreated" />
              </StackPanel>
            </UserControl>
            """;

        var result = await fixture.Host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml)), Ct);

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task GridLayout_ReportsRowsInsidePaddingAndSpacing()
    {
        var xaml = $"""
            <Grid {RenderXaml.Namespaces} Width="200" Height="200" Padding="10" RowSpacing="20">
              <Grid.RowDefinitions><RowDefinition Height="50" /><RowDefinition Height="*" /></Grid.RowDefinitions>
            </Grid>
            """;

        var result = await fixture.Host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml)), Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(PanelKind.Grid, result.Root!.Panel!.Kind);
        Assert.Equal([10, 80, 190], result.Root.Panel.RowOffsets!);
    }

    [Fact]
    public async Task XamlError_ReportsLineAndColumn()
    {
        var xaml = $"""
            <UserControl {RenderXaml.Namespaces}>
              <StackPanel>
                <NoSuchControl />
              </StackPanel>
            </UserControl>
            """;

        var result = await fixture.Host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml)), Ct);

        Assert.False(result.Success);
        Assert.Equal(3, result.Error!.Line);
        Assert.DoesNotContain("text associated with this error code", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ThemeVariantKind.Light, 200)]
    [InlineData(ThemeVariantKind.Dark, 60)]
    public async Task Theme_ChangesThePageBackground(ThemeVariantKind theme, int around)
    {
        var xaml = $"""<UserControl {RenderXaml.Namespaces} Width="40" Height="40" />""";

        var result = await fixture.Host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml), Theme: theme), Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.InRange(RenderXaml.Pixel(result, 20, 20).R, around - 60, around + 60);
    }
}
