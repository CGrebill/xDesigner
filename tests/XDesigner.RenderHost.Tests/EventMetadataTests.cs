using XDesigner.Protocol.Metadata;
using XDesigner.RenderHost.Metadata;

namespace XDesigner.RenderHost.Tests;

public class EventMetadataTests(RenderHostFixture host)
{
    private Task<Dictionary<string, EventInfoDto>> EventsAsync(string fullName) => host.Runtime.InvokeAsync(() =>
    {
        var type = new MetadataProvider().FindType(fullName) ?? throw new InvalidOperationException(fullName);
        return new EventMetadataProvider().For(type).ToDictionary(e => e.Name);
    });

    [Fact]
    public async Task Button_Click_IsARoutedEventWithRoutedEventArgs()
    {
        var events = await EventsAsync("Avalonia.Controls.Button");

        var click = events["Click"];
        Assert.True(click.IsRouted);
        Assert.Equal("object?", click.SenderType);
        Assert.Equal("RoutedEventArgs", click.ArgsType);
        Assert.Equal("Avalonia.Interactivity", click.ArgsNamespace);
        Assert.Contains("EventArgs", click.ArgsBaseTypes);

        Assert.Equal("PointerPressedEventArgs", events["PointerPressed"].ArgsType);
        Assert.Contains("RoutedEventArgs", events["PointerPressed"].ArgsBaseTypes);
    }

    [Fact]
    public async Task TextBox_And_Window_EventsHaveTheirOwnArgs()
    {
        var textBox = await EventsAsync("Avalonia.Controls.TextBox");
        Assert.Equal("TextChangedEventArgs", textBox["TextChanged"].ArgsType);

        var window = await EventsAsync("Avalonia.Controls.Window");
        Assert.Equal(("EventArgs", "System"), (window["Opened"].ArgsType, window["Opened"].ArgsNamespace));
        Assert.False(window["Opened"].IsRouted);
        Assert.Equal("WindowClosingEventArgs", window["Closing"].ArgsType);
    }

    [Theory]
    [InlineData(typeof(int?), true, "int?")]
    [InlineData(typeof(List<string>), true, "System.Collections.Generic.List<string>")]
    [InlineData(typeof(Dictionary<string, int>.KeyCollection), false, "Dictionary<string, int>.KeyCollection")]
    [InlineData(typeof(EventArgs), false, "EventArgs")]
    public void CSharpName_WritesCSharpSyntax(Type type, bool qualify, string expected) =>
        Assert.Equal(expected, EventMetadataProvider.CSharpName(type, qualify));
}
