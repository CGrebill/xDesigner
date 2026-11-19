using System.Globalization;
using System.Text;
using System.Xml.Linq;
using XDesigner.Core.Xaml;

namespace XDesigner.Core.Tests;

public class XamlDocumentTests
{
    private static readonly XNamespace Av = XamlNamespaces.Avalonia;
    private static readonly XNamespace X = XamlNamespaces.Xaml;

    private const string MultiLine =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
        "<!-- leading comment -->\r\n" +
        "<Window xmlns=\"https://github.com/avaloniaui\"\r\n" +
        "        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'\r\n" +
        "        x:Class=\"App.MainWindow\"\r\n" +
        "        Title=\"Tom &amp; Jerry\"   Width = \"400\">\r\n" +
        "\t<Grid RowDefinitions=\"Auto,*\">\r\n" +
        "    <Grid.ColumnDefinitions><ColumnDefinition Width=\"*\"/></Grid.ColumnDefinitions>\r\n" +
        "    <TextBlock Text=\"&#xE737; glyph\" />\r\n" +
        "    <Button x:Name=\"ok\"\r\n" +
        "            Content=\"OK\"/>\r\n" +
        "    <!-- a comment -->\r\n" +
        "    <TextBlock><![CDATA[a < b]]></TextBlock>\r\n" +
        "    <Border></Border>\r\n" +
        "  </Grid>\r\n" +
        "</Window>\r\n";

    /// <summary>Text the writer used to re-escape: entities, a raw '&gt;', an encoded line break, PIs, a comment after the root.</summary>
    private const string TextEdgeCases =
        "<Window xmlns=\"https://github.com/avaloniaui\">\r\n" +
        "  <StackPanel>\r\n" +
        "    <TextBlock>a > b &quot;quoted&quot; &apos;x&apos; &#160;nbsp &#x2014;</TextBlock>\r\n" +
        "    <TextBox Name=\"box\"><TextBox.Text>one&#13;&#10;two</TextBox.Text></TextBox>\r\n" +
        "    <!-- crlf -->\r\n" +
        "    <?designer?>\r\n" +
        "    <?designer keep this?>\r\n" +
        "  </StackPanel>\r\n" +
        "</Window>\r\n" +
        "<!-- after the root -->\r\n";

    public static TheoryData<string> RoundTripCorpus()
    {
        var data = new TheoryData<string> { MultiLine, MultiLine.Replace("\r\n", "\n"), "<UserControl xmlns=\"https://github.com/avaloniaui\"/>", TextEdgeCases };
        data.Add(TextEdgeCases.Replace("\r\n", "\n"));
        data.Add(TextEdgeCases.Replace("\r\n", "\n").Replace("<!-- crlf -->\n", "<!-- crlf -->\r\n")); // mixed line endings
        var sample = Path.Combine(RepoRoot(), "samples", "SampleAvaloniaApp");
        foreach (var file in Directory.EnumerateFiles(sample, "*.axaml", SearchOption.AllDirectories))
        {
            data.Add(File.ReadAllText(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RoundTripCorpus))]
    public void UneditedDocument_RoundTripsByteForByte(string text)
    {
        Assert.Equal(text, XamlDocument.Parse(text).ToXaml());
    }

    [Fact]
    public void EditingAnAttribute_KeepsUneditedTextVerbatim()
    {
        var doc = XamlDocument.Parse(TextEdgeCases);

        doc.Xml.Descendants(Av + "TextBox").Single().SetAttributeValue("Name", "renamed");

        Assert.Equal(TextEdgeCases.Replace("Name=\"box\"", "Name=\"renamed\""), doc.ToXaml());
    }

    [Fact]
    public void NewTextWithCarriageReturn_RoundTripsAsOneLineBreak()
    {
        var doc = XamlDocument.Parse(TextEdgeCases);
        var text = doc.Xml.Descendants(Av + "TextBox.Text").Single();

        text.Value = "three\r\nfour";

        var reparsed = XDocument.Parse(doc.ToXaml(), LoadOptions.PreserveWhitespace);
        Assert.Equal("three\r\nfour", reparsed.Descendants(Av + "TextBox.Text").Single().Value);
    }

    [Fact]
    public void ChangedText_IsRewritten_OtherTextIsNot()
    {
        var doc = XamlDocument.Parse(TextEdgeCases);
        var block = doc.Xml.Descendants(Av + "TextBlock").Single();

        block.Value = "x < y";

        var expected = TextEdgeCases.Replace("a > b &quot;quoted&quot; &apos;x&apos; &#160;nbsp &#x2014;", "x &lt; y");
        Assert.Equal(expected, doc.ToXaml());
    }

    [Fact]
    public void ChangingOneAttribute_RewritesOnlyThatAttribute()
    {
        var doc = XamlDocument.Parse(MultiLine);

        doc.Xml.Root!.SetAttributeValue("Width", "640");

        var expected = MultiLine.Replace("Width = \"400\"", "Width=\"640\"");
        Assert.Equal(expected, doc.ToXaml());
    }

    [Fact]
    public void ChangingAttributeWrittenWithSingleQuotes_KeepsQuoteStyle()
    {
        var doc = XamlDocument.Parse(MultiLine);

        doc.Xml.Root!.SetAttributeValue(XNamespace.Xmlns + "x", "urn:other");

        Assert.Contains("xmlns:x='urn:other'", doc.ToXaml());
    }

    [Fact]
    public void AddingAttribute_ToMultiLineTag_FollowsItsLayout()
    {
        var doc = XamlDocument.Parse(MultiLine);
        var button = doc.Xml.Descendants(Av + "Button").Single();

        button.SetAttributeValue("Width", "80");

        // The tag's own "/>" style (no space) is kept too.
        Assert.Contains("<Button x:Name=\"ok\"\r\n            Content=\"OK\"\r\n            Width=\"80\"/>", doc.ToXaml());
    }

    [Fact]
    public void AddingAttribute_ToSingleLineTag_AppendsWithSpace()
    {
        var doc = XamlDocument.Parse(MultiLine);
        var grid = doc.Xml.Descendants(Av + "Grid").Single();

        grid.SetAttributeValue("Margin", "8");

        Assert.Contains("\t<Grid RowDefinitions=\"Auto,*\" Margin=\"8\">\r\n", doc.ToXaml());
    }

    [Fact]
    public void RemovingAttribute_DropsItAndItsLeadingWhitespace()
    {
        var doc = XamlDocument.Parse(MultiLine);

        doc.Xml.Root!.Attribute(X + "Class")!.Remove();

        Assert.Contains("        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'\r\n        Title=", doc.ToXaml());
        Assert.DoesNotContain("x:Class", doc.ToXaml());
    }

    [Fact]
    public void AddingChild_ToSelfClosingElement_OpensItUp()
    {
        var doc = XamlDocument.Parse(MultiLine);
        var button = doc.Xml.Descendants(Av + "Button").Single();

        button.Add(new XElement(Av + "TextBlock", new XAttribute("Text", "hi")));

        Assert.Contains("<Button x:Name=\"ok\"\r\n            Content=\"OK\"><TextBlock Text=\"hi\" /></Button>", doc.ToXaml());
        Assert.NotNull(XDocument.Parse(doc.ToXaml()));
    }

    [Fact]
    public void ValuesNeedingEscapes_AreEscaped()
    {
        var doc = XamlDocument.Parse(MultiLine);

        doc.Xml.Root!.SetAttributeValue("Title", "a \"quoted\" <b> & c");

        Assert.Contains("Title=\"a &quot;quoted&quot; &lt;b> &amp; c\"", doc.ToXaml());
        Assert.Equal("a \"quoted\" <b> & c", XDocument.Parse(doc.ToXaml()).Root!.Attribute("Title")!.Value);
    }

    [Fact]
    public void MovedElement_KeepsItsOriginalFormatting()
    {
        var doc = XamlDocument.Parse(MultiLine);
        var button = doc.Xml.Descendants(Av + "Button").Single();
        var border = doc.Xml.Descendants(Av + "Border").Single();

        button.Remove();
        border.Add(button);

        Assert.Contains("<Border><Button x:Name=\"ok\"\r\n            Content=\"OK\"/></Border>", doc.ToXaml());
    }

    [Fact]
    public void Ids_AreStableAndCoverObjectElementsOnly()
    {
        var doc = XamlDocument.Parse(MultiLine);

        var names = doc.AllNodes.Select(n => n.TypeName).ToList();
        Assert.Equal(["Window", "Grid", "ColumnDefinition", "TextBlock", "Button", "TextBlock", "Border"], names);

        var button = doc.AllNodes.Single(n => n.TypeName == "Button");
        var id = button.Id;
        button.Element.Remove();
        doc.Xml.Root!.Add(button.Element);

        Assert.Equal(id, doc.NodeFor(button.Element).Id);
        Assert.Same(button, doc.FindById(id));
    }

    [Fact]
    public void FindById_ReturnsNullForRemovedElements()
    {
        var doc = XamlDocument.Parse(MultiLine);
        var border = doc.AllNodes.Single(n => n.TypeName == "Border");

        border.Element.Remove();

        Assert.Null(doc.FindById(border.Id));
    }

    [Fact]
    public void Outline_SkipsDefinitionsButIncludesContentPropertyElements()
    {
        var doc = XamlDocument.Parse("""
            <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Grid>
                <Grid.RowDefinitions><RowDefinition /></Grid.RowDefinitions>
                <Grid.Resources><SolidColorBrush x:Key="b" /></Grid.Resources>
                <Button><Button.Content><TextBlock x:Name="label" /></Button.Content></Button>
              </Grid>
            </Window>
            """);

        var grid = doc.Root.Children.Single();
        Assert.Equal(["[Button]"], grid.Children.Select(c => c.DisplayName));
        var label = grid.Children.Single().Children.Single();
        Assert.Equal("label", label.DisplayName);
        Assert.Equal([label.Id, grid.Children.Single().Id, grid.Id, doc.Root.Id], label.AncestorsAndSelf().Select(n => n.Id));
    }

    [Fact]
    public void Changed_ReportsAffectedElementAndStructure()
    {
        var doc = XamlDocument.Parse(MultiLine);
        var changes = new List<XamlDocumentChange>();
        doc.Changed += (_, c) => changes.Add(c);
        var grid = doc.AllNodes.Single(n => n.TypeName == "Grid");

        grid.Element.SetAttributeValue("Margin", "4");
        grid.Element.Add(new XElement(Av + "Button"));

        Assert.Equal(2, changes.Count);
        Assert.Equal([grid.Id], changes[0].AffectedIds);
        Assert.False(changes[0].StructureChanged);
        Assert.True(changes[1].StructureChanged);
        Assert.Equal(2, doc.Revision);
    }

    [Fact]
    public void BatchChanges_RaisesOnce()
    {
        var doc = XamlDocument.Parse(MultiLine);
        var count = 0;
        doc.Changed += (_, _) => count++;

        using (doc.BatchChanges())
        {
            doc.Xml.Root!.SetAttributeValue("Width", "1");
            doc.Xml.Root!.SetAttributeValue("Height", "2");
        }

        Assert.Equal(1, count);
    }

    [Fact]
    public void RenderCopy_UsesStableIds()
    {
        var doc = XamlDocument.Parse(MultiLine);
        var button = doc.AllNodes.Single(n => n.TypeName == "Button");

        var copy = XDocument.Parse(doc.BuildRenderCopy());

        var renderedButton = copy.Descendants(Av + "Button").Single();
        Assert.Equal(button.Id.ToString(CultureInfo.InvariantCulture), renderedButton.Attribute(RenderCopyBuilder.DesignIdAttribute)!.Value);
    }

    [Fact]
    public void LoadAndSave_PreserveBomAndEncoding()
    {
        var dir = Directory.CreateTempSubdirectory("xdesigner-doc-");
        try
        {
            var path = Path.Combine(dir.FullName, "a.axaml");
            File.WriteAllText(path, MultiLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var original = File.ReadAllBytes(path);

            XamlDocument.Load(path).Save(path);

            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XDesigner.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
