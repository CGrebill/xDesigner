using XDesigner.Protocol.Metadata;
using XDesigner.Protocol.Rendering;
using XDesigner.RenderHost.Metadata;
using XDesigner.RenderHost.Rendering;

namespace XDesigner.RenderHost.Tests;

public class PropertyMetadataTests(RenderHostFixture host)
{
    private Task<PropertyInfoDto[]> PropertiesAsync(string fullName) => host.Runtime.InvokeAsync(() =>
    {
        var type = new MetadataProvider().FindType(fullName) ?? throw new InvalidOperationException(fullName);
        return new PropertyMetadataProvider().For(type).Select(p => p.Info).ToArray();
    });

    [Fact]
    public async Task Button_HasTypedPropertiesFromItsHierarchy()
    {
        var props = (await PropertiesAsync("Avalonia.Controls.Button")).ToDictionary(p => p.Key);

        Assert.Equal(ValueKind.Object, props["Content"].Kind);
        Assert.Equal(ValueKind.Brush, props["Background"].Kind);
        Assert.Equal(ValueKind.Thickness, props["Margin"].Kind);
        Assert.Equal(ValueKind.Double, props["Width"].Kind);
        Assert.Equal(ValueKind.Double, props["FontSize"].Kind);
        Assert.Equal(ValueKind.Bool, props["IsEnabled"].Kind);
        Assert.Equal(ValueKind.CornerRadius, props["CornerRadius"].Kind);
        Assert.Equal(ValueKind.FontFamily, props["FontFamily"].Kind);
        Assert.Equal(ValueKind.Enum, props["HorizontalAlignment"].Kind);
        Assert.Contains("Stretch", props["HorizontalAlignment"].EnumValues!);
        Assert.Equal(ValueKind.Enum, props["FontWeight"].Kind);
        Assert.Contains("Bold", props["FontWeight"].EnumValues!);
        Assert.Equal(ValueKind.Unsupported, props["Template"].Kind);
        Assert.False(props.ContainsKey("Bounds")); // read-only
    }

    [Fact]
    public async Task CommonAttachedProperties_AreIncluded()
    {
        var props = (await PropertiesAsync("Avalonia.Controls.TextBlock")).ToDictionary(p => p.Key);

        Assert.True(props["Grid.Row"].IsAttached);
        Assert.Equal(ValueKind.Int, props["Grid.Row"].Kind);
        Assert.Equal(ValueKind.Double, props["Canvas.Left"].Kind);
        Assert.Equal(ValueKind.Enum, props["DockPanel.Dock"].Kind);
        Assert.Equal(ValueKind.Object, props["ToolTip.Tip"].Kind);
        Assert.Equal(ValueKind.String, props["Text"].Kind);
    }

    [Fact]
    public async Task GridDefinitions_AndUserControlProperties_AreTextEditable()
    {
        var grid = (await PropertiesAsync("Avalonia.Controls.Grid")).ToDictionary(p => p.Key);
        Assert.Equal(ValueKind.String, grid["RowDefinitions"].Kind);

        var stars = (await PropertiesAsync("SampleAvaloniaApp.Controls.StarRating")).ToDictionary(p => p.Key);
        Assert.Equal(ValueKind.Int, stars["Value"].Kind);
        Assert.Equal("StarRating", stars["Value"].OwnerName);
    }

    [Fact]
    public async Task Values_ReportEffectiveValuesAndWhereTheyComeFrom()
    {
        var values = await host.Runtime.InvokeAsync(() =>
        {
            var renderer = new XamlRenderer(host.Runtime.UserAssembly);
            var result = renderer.Render(new RenderRequest(1, RenderXaml.SampleMainWindow, 1, ThemeVariantKind.Light, null));
            Assert.True(result.Success, result.Error?.Message);

            var button = ValueReader.Find(renderer.CurrentRoot, 7)!;
            return ValueReader.Read(button, new PropertyMetadataProvider().For(button.GetType())).ToDictionary(v => v.Key);
        });

        Assert.Equal(("Click me", ValueSource.Local), (values["Content"].Value, values["Content"].Source));
        Assert.Equal(("2", ValueSource.Local), (values["Grid.Row"].Value, values["Grid.Row"].Source));
        Assert.Equal(("Left", ValueSource.Local), (values["HorizontalAlignment"].Value, values["HorizontalAlignment"].Source));
        Assert.Equal("NaN", values["Width"].Value);  // Auto
        Assert.Equal(ValueSource.Default, values["Width"].Source);
        Assert.Equal(ValueSource.Style, values["Background"].Source); // from the Fluent theme
        Assert.StartsWith("#", values["Background"].Value);
        Assert.Equal(ValueSource.Inherited, values["FontSize"].Source);
    }

    [Fact]
    public async Task Find_ReturnsNullForUnknownIds()
    {
        var found = await host.Runtime.InvokeAsync(() =>
        {
            var renderer = new XamlRenderer(host.Runtime.UserAssembly);
            renderer.Render(new RenderRequest(1, RenderXaml.SampleMainWindow, 1, ThemeVariantKind.Default, null));
            return (Root: ValueReader.Find(renderer.CurrentRoot, 1) is not null, Missing: ValueReader.Find(renderer.CurrentRoot, 999) is null);
        });

        Assert.True(found.Root);
        Assert.True(found.Missing);
    }

    [Theory]
    [InlineData(8.0, "8")]
    [InlineData(0.1 + 0.2, "0.3")]
    [InlineData(double.NaN, "NaN")]
    public void Format_UsesXamlText(double value, string expected) => Assert.Equal(expected, ValueReader.Format(value));

    [Fact]
    public void Format_Thickness_Collapses()
    {
        Assert.Equal("8", ValueReader.Format(new Avalonia.Thickness(8)));
        Assert.Equal("8,4", ValueReader.Format(new Avalonia.Thickness(8, 4)));
        Assert.Equal("1,2,3,4", ValueReader.Format(new Avalonia.Thickness(1, 2, 3, 4)));
        Assert.Equal("#FF0000", ValueReader.Format(Avalonia.Media.Colors.Red));
        Assert.Equal("#80FF0000", ValueReader.Format(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Red, 0.5)));
    }
}
