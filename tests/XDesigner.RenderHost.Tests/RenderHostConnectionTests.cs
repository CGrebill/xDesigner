using XDesigner.Core.Rendering;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Rendering;

namespace XDesigner.RenderHost.Tests;

/// <summary>End to end over the named pipe, exactly as the designer app talks to the host (§4, §15.2).</summary>
public class RenderHostConnectionTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RendersTheSampleFileAsWritten_WithClassAndEventHandlers()
    {
        await using var host = await RenderHostConnection.StartAsync(
            new RenderHostStartInfo(TestPaths.RenderHostExe, TestPaths.SampleAssembly), cancellationToken: Ct);

        Assert.Equal("SampleAvaloniaApp.App", host.Info.ApplicationType);
        Assert.Equal("SampleAvaloniaApp", host.Info.UserAssemblyName);
        Assert.True(host.Info.IsDesignMode);
        Assert.StartsWith("12.", host.Info.AvaloniaVersion);

        // The real file: x:Class and Click="button1_Click" are stripped by the host.
        var source = await File.ReadAllTextAsync(Path.Combine(TestPaths.RepoRoot, "samples", "SampleAvaloniaApp", "MainWindow.axaml"), Ct);
        var result = await host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(source), 1.25, DocumentId: "main"), Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal((800, 525), (result.PixelWidth, result.PixelHeight));
        Assert.Equal(800 * 525 * 4, result.Bgra!.Length);
        Assert.Equal(7, Count(result.Root!));
    }

    [Fact]
    public async Task ProjectImages_ResolveAgainstTheDocumentsBaseUri()
    {
        await using var host = await RenderHostConnection.StartAsync(
            new RenderHostStartInfo(TestPaths.RenderHostExe, TestPaths.SampleAssembly), cancellationToken: Ct);
        var xaml = $"""
            <UserControl {RenderXaml.Namespaces} Width="40" Height="40">
              <Image Source="/Assets/red.png" Stretch="Fill" />
            </UserControl>
            """;

        // Assets/red.png is an AvaloniaResource of the sample, which the host loaded from memory.
        var result = await host.Host.RenderAsync(
            new RenderRequest(1, RenderCopyBuilder.Build(xaml), BaseUri: "avares://SampleAvaloniaApp/MainWindow.axaml"), Ct);

        Assert.True(result.Success, result.Error?.Message);
        var centre = ((20 * result.PixelWidth) + 20) * 4;
        Assert.Equal((0, 0, 255), (result.Bgra![centre], result.Bgra[centre + 1], result.Bgra[centre + 2])); // BGRA: red
    }

    [Fact]
    public async Task UncompiledEventHandler_StillRenders()
    {
        await using var host = await RenderHostConnection.StartAsync(
            new RenderHostStartInfo(TestPaths.RenderHostExe, TestPaths.SampleAssembly), cancellationToken: Ct);
        var xaml = $"""
            <UserControl {RenderXaml.Namespaces} x:Class="SampleAvaloniaApp.Controls.ContactCard">
              <StackPanel>
                <Button Click="handlerTheDesignerJustCreated" Button.Click="another" />
                <local:StarRating PointerPressed="stars_PointerPressed" />
              </StackPanel>
            </UserControl>
            """;

        var result = await host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml)), Ct);

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task StylesAndResources_DontReceiveDesignIds()
    {
        await using var host = await RenderHostConnection.StartAsync(
            new RenderHostStartInfo(TestPaths.RenderHostExe, null), cancellationToken: Ct);
        var xaml = $"""
            <UserControl {RenderXaml.Namespaces}>
              <UserControl.Styles>
                <Style Selector="Button"><Setter Property="Margin" Value="4" /></Style>
              </UserControl.Styles>
              <UserControl.Resources>
                <SolidColorBrush x:Key="Accent" Color="Red" />
              </UserControl.Resources>
              <Button Content="Styled" />
            </UserControl>
            """;

        var result = await host.Host.RenderAsync(new RenderRequest(1, RenderCopyBuilder.Build(xaml)), Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("XDesigner.RenderHost.Hosting.DesignApp", host.Info.ApplicationType);
    }

    [Fact]
    public async Task MissingUserAssembly_FailsSetupWithExitCode3()
    {
        var ex = await Assert.ThrowsAsync<RenderHostStartException>(() => RenderHostConnection.StartAsync(
            new RenderHostStartInfo(TestPaths.RenderHostExe, @"C:\does\not\exist.dll"), cancellationToken: Ct));

        output.WriteLine(ex.ErrorOutput);
        Assert.True(ex.IsSetupFailure);
        Assert.Contains("exist.dll", ex.ErrorOutput);
    }

    [Fact]
    public async Task KilledHost_RaisesDisconnected()
    {
        var host = await RenderHostConnection.StartAsync(new RenderHostStartInfo(TestPaths.RenderHostExe, null), cancellationToken: Ct);
        var disconnected = new TaskCompletionSource<string>();
        host.Disconnected += (_, reason) => disconnected.TrySetResult(reason);

        System.Diagnostics.Process.GetProcessById(host.ProcessId).Kill();

        var reason = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        output.WriteLine(reason);
        Assert.False(host.IsAlive);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_ShutsTheHostDown()
    {
        var host = await RenderHostConnection.StartAsync(new RenderHostStartInfo(TestPaths.RenderHostExe, null), cancellationToken: Ct);
        var process = System.Diagnostics.Process.GetProcessById(host.ProcessId);

        await host.DisposeAsync();

        await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(process.HasExited);
    }

    private static int Count(ElementLayout e) => 1 + e.Children.Sum(Count);
}
