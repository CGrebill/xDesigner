using System.Xml.Linq;
using XDesigner.Core.Xaml;
using XDesigner.Protocol;

namespace XDesigner.Core.Tests;

public class RenderCopyBuilderTests
{
    [Fact]
    public void RenderCopy_KeepsTheRootOnItsOriginalLine()
    {
        const string source = "<?xml version=\"1.0\"?>\r\n<!-- license -->\r\n<UserControl xmlns=\"https://github.com/avaloniaui\">\r\n  <Button />\r\n</UserControl>\r\n";

        var copy = XamlDocument.Parse(source).BuildRenderCopy();

        var lines = copy.Split('\n');
        Assert.StartsWith("<UserControl", lines[2]);
        Assert.Contains("<Button", lines[3]);
    }

    private const string Xaml = """
        <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="App.MainWindow">
          <Grid>
            <Grid.RowDefinitions>
              <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>
            <Button x:Name="b" Click="b_Click"><x:String>hi</x:String></Button>
          </Grid>
        </Window>
        """;

    [Fact]
    public void AddsIdsToObjectElementsInDocumentOrder()
    {
        var copy = XDocument.Parse(RenderCopyBuilder.Build(Xaml));

        var ids = copy.Descendants()
            .Select(e => (e.Name.LocalName, Id: (string?)e.Attribute(RenderCopyBuilder.DesignIdAttribute)))
            .ToList();
        Assert.Equal(
            [("Window", "1"), ("Grid", "2"), ("Grid.RowDefinitions", null), ("RowDefinition", "3"), ("Button", "4"), ("String", null)],
            ids);
        Assert.Equal(ProtocolInfo.DesignIdXmlNamespace, copy.Root!.Attribute(XNamespace.Xmlns + "dsn")!.Value);
    }

    [Fact]
    public void LeavesTheSourceUntouched_AndKeepsClassAndEventsForTheHostToStrip()
    {
        var source = XDocument.Parse(Xaml, LoadOptions.PreserveWhitespace);
        var before = source.ToString(SaveOptions.DisableFormatting);

        var copy = RenderCopyBuilder.Build(source, _ => 7);

        Assert.Equal(before, source.ToString(SaveOptions.DisableFormatting));
        Assert.Contains("x:Class=\"App.MainWindow\"", copy);
        Assert.Contains("Click=\"b_Click\"", copy);
    }

    [Fact]
    public void PreservesLineStructure()
    {
        var copy = RenderCopyBuilder.Build(Xaml);

        Assert.Equal(Xaml.Split('\n').Length, copy.Split('\n').Length);
    }

    /// <summary>Tags whose attributes span lines keep them there, so later elements stay on their lines (review M21).</summary>
    [Fact]
    public void MultiLineStartTags_KeepEveryLaterElementOnItsLine()
    {
        var source = """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <StackPanel>
                <Button x:Name="a"
                        Content="A"
                        Width="80" />
                <Bad />
              </StackPanel>
            </UserControl>
            """.ReplaceLineEndings("\n");

        var copy = XamlDocument.Parse(source).BuildRenderCopy();

        var lines = copy.Split('\n');
        Assert.Contains("Content=\"A\"", lines[4], StringComparison.Ordinal);
        Assert.Contains("<Bad", lines[6], StringComparison.Ordinal);
        Assert.Equal(source.Split('\n').Length, lines.Length);

        // And through a second parse with line info, as the render hosts do.
        var reparsed = XDocument.Parse(copy, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        var again = XDesigner.Protocol.Rendering.LineKeepingXml.Write(reparsed.Root!).Split('\n');
        Assert.Contains("<Bad", again[6], StringComparison.Ordinal);
    }
}
