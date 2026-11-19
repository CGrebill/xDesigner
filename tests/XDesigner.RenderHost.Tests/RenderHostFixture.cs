using XDesigner.Protocol.Rendering;
using XDesigner.RenderHost.Hosting;
using XDesigner.RenderHost.Rendering;

[assembly: AssemblyFixture(typeof(XDesigner.RenderHost.Tests.RenderHostFixture))]

namespace XDesigner.RenderHost.Tests;

/// <summary>
/// One Avalonia session for the whole test assembly (Avalonia can only be set up once per process),
/// loaded with the sample app exactly as the designer would load a user project.
/// </summary>
public sealed class RenderHostFixture : IDisposable
{
    public RenderHostFixture()
    {
        Runtime = HostRuntime.Start(new HostStartOptions(TestPaths.SampleAssembly));
    }

    internal HostRuntime Runtime { get; }

    /// <summary>Renders on the UI thread with a fresh renderer.</summary>
    internal Task<RenderResult> RenderAsync(string xaml, double scale = 1.0, ThemeVariantKind theme = ThemeVariantKind.Default, SizeDto? size = null) =>
        Runtime.InvokeAsync(() => new XamlRenderer(Runtime.UserAssembly).Render(new RenderRequest(1, xaml, scale, theme, size)));

    public void Dispose() => Runtime.Dispose();
}

/// <summary>Builds render-copy XAML the way the designer's Core will (§5.4).</summary>
internal static class RenderXaml
{
    public const string Namespaces =
        """xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:d="http://schemas.microsoft.com/expression/blend/2008" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" mc:Ignorable="d" xmlns:local="using:SampleAvaloniaApp.Controls" xmlns:t="using:XDesigner.RenderHost.Tests" xmlns:dsn="clr-namespace:XDesigner.RenderHost.DesignTime;assembly=XDesigner.RenderHost" """;

    /// <summary>The sample's MainWindow as the designer would send it: design ids added, x:Class and Click removed.</summary>
    public const string SampleMainWindow =
        $"""
        <Window {Namespaces} d:DesignWidth="640" d:DesignHeight="420" Title="Sample Avalonia App" Width="640" Height="420" dsn:DesignId.Value="1">
          <Grid RowDefinitions="Auto,*,Auto" Margin="16" dsn:DesignId.Value="2">
            <TextBlock x:Name="titleText" Grid.Row="0" Text="Hello from Avalonia 12" FontSize="24" FontWeight="SemiBold" dsn:DesignId.Value="3" />
            <StackPanel Grid.Row="1" Spacing="12" Margin="0,16,0,0" dsn:DesignId.Value="4">
              <local:ContactCard x:Name="contactCard1" dsn:DesignId.Value="5" />
              <local:StarRating x:Name="starRating1" Value="3" Maximum="5" dsn:DesignId.Value="6" />
            </StackPanel>
            <Button x:Name="button1" Grid.Row="2" Content="Click me" HorizontalAlignment="Left" dsn:DesignId.Value="7" />
          </Grid>
        </Window>
        """;
}
