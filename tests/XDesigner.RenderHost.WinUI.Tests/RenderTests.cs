using XDesigner.Core.Xaml;
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
