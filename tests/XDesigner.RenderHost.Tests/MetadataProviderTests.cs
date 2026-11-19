using XDesigner.Protocol.Metadata;
using XDesigner.RenderHost.Metadata;

namespace XDesigner.RenderHost.Tests;

public class MetadataProviderTests(RenderHostFixture host)
{
    private Task<ControlTypeInfo[]> TypesAsync() => host.Runtime.InvokeAsync(() => new MetadataProvider().GetControlTypes());

    [Theory]
    [InlineData("Avalonia.Controls.Button", ContentModel.SingleContent, "Click")]
    [InlineData("Avalonia.Controls.Grid", ContentModel.Children, "Loaded")]
    [InlineData("Avalonia.Controls.StackPanel", ContentModel.Children, "Loaded")]
    [InlineData("Avalonia.Controls.Border", ContentModel.SingleChild, "Loaded")]
    [InlineData("Avalonia.Controls.ListBox", ContentModel.Items, "SelectionChanged")]
    [InlineData("Avalonia.Controls.TextBlock", ContentModel.Inlines, "Loaded")]
    [InlineData("Avalonia.Controls.TextBox", ContentModel.None, "TextChanged")]
    [InlineData("Avalonia.Controls.CheckBox", ContentModel.SingleContent, "IsCheckedChanged")]
    [InlineData("Avalonia.Controls.Slider", ContentModel.None, "ValueChanged")]
    [InlineData("Avalonia.Controls.Window", ContentModel.SingleContent, "Opened")]
    [InlineData("Avalonia.Controls.UserControl", ContentModel.SingleContent, "Loaded")]
    [InlineData("Avalonia.Controls.Viewbox", ContentModel.SingleChild, "Loaded")]
    public async Task BuiltInControls(string fullName, ContentModel content, string defaultEvent)
    {
        var type = (await TypesAsync()).Single(t => t.FullName == fullName);

        Assert.True(type.IsBuiltIn);
        Assert.True(type.IsCreatable);
        Assert.Equal(ControlTypeInfo.AvaloniaXmlNamespace, type.XmlNamespace);
        Assert.Equal(content, type.Content);
        Assert.Equal(defaultEvent, type.DefaultEventName);
    }

    [Fact]
    public async Task UserProjectControls_UseUsingNamespace_AndUserControlsAreNotDropTargets()
    {
        var types = await TypesAsync();

        var card = types.Single(t => t.FullName == "SampleAvaloniaApp.Controls.ContactCard");
        Assert.False(card.IsBuiltIn);
        Assert.Equal("using:SampleAvaloniaApp.Controls", card.XmlNamespace);
        Assert.Equal("SampleAvaloniaApp", card.AssemblyName);
        Assert.Equal(ContentModel.None, card.Content);
        Assert.True(card.DerivesFrom("Avalonia.Controls.UserControl"));

        var stars = types.Single(t => t.Name == "StarRating");
        Assert.True(stars.IsCreatable);
        Assert.Equal(ContentModel.None, stars.Content);
    }

    [Fact]
    public async Task AbstractTypes_AreListedButNotCreatable()
    {
        var types = await TypesAsync();

        Assert.True(types.Single(t => t.FullName == "Avalonia.Controls.Panel").IsCreatable);
        Assert.False(types.Single(t => t.FullName == "Avalonia.Controls.Primitives.RangeBase").IsCreatable);
    }
}
