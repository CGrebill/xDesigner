using System.Xml.Linq;
using XDesigner.Core.Editing;
using XDesigner.Core.Frameworks;
using XDesigner.Core.Xaml;

namespace XDesigner.Core.Tests;

/// <summary>
/// Files that use namespaces in less common ways (§5.3): one namespace bound to two prefixes, the "other" x: or d: URI.
/// Edits must never produce a malformed file (review H2, H3) and must read x:Class/x:Name under the URI the file uses (M12).
/// </summary>
public class NamespaceEditingTests
{
    private const string Avalonia = FrameworkProfile.AvaloniaNamespace;
    private const string Maui = FrameworkProfile.MauiNamespace;

    private static void AssertWellFormed(string xaml) => XDocument.Parse(xaml);

    [Fact]
    public void EndTag_UsesTheStartTagsPrefix_WhenANamespaceHasTwoPrefixes()
    {
        var text = $"""<UserControl xmlns="{Avalonia}" xmlns:a="using:Foo" xmlns:b="using:Foo"><b:Panel><TextBlock /></b:Panel></UserControl>""";
        var doc = XamlDocument.Parse(text);
        Assert.Equal(text, doc.ToXaml());

        // Edited start tag: rebuilt, and the end tag still matches it.
        var panel = doc.AllNodes.Single(n => n.TypeName == "Panel");
        new UndoManager(doc).Execute(new SetAttributeCommand(panel.Id, "Tag", "1"));
        var saved = doc.ToXaml();
        AssertWellFormed(saved);
        Assert.Contains("<b:Panel Tag=\"1\">", saved, StringComparison.Ordinal);
        Assert.Contains("</b:Panel>", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void EndTag_StaysUnprefixed_WhenTheDefaultNamespaceAlsoHasAPrefix()
    {
        var text = $"""<UserControl xmlns="{Avalonia}" xmlns:av="{Avalonia}"><Button>Hi</Button></UserControl>""";
        var doc = XamlDocument.Parse(text);
        Assert.Equal(text, doc.ToXaml());
        var button = doc.AllNodes.Single(n => n.TypeName == "Button");
        new UndoManager(doc).Execute(new SetAttributeCommand(button.Id, "Tag", "1"));
        Assert.Contains("<Button Tag=\"1\">Hi</Button>", doc.ToXaml(), StringComparison.Ordinal);
    }

    [Fact]
    public void KeepPreviewSize_InAMauiFileWithBlendsDesignNamespace_ReusesItsPrefix()
    {
        var text = $"""
            <ContentView xmlns="{Maui}" xmlns:x="{FrameworkProfile.Xaml2009Namespace}"
                         xmlns:d="{FrameworkProfile.BlendDesignNamespace}" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                         mc:Ignorable="d" d:DesignWidth="400" x:Class="App.V" />
            """;
        var doc = XamlDocument.Parse(text);
        doc.Profile = FrameworkProfile.Maui11;
        var undo = new UndoManager(doc);
        Assert.True(new DesignerActions(doc, undo).SetDesignSize(500, 300, isWindow: false));

        var saved = doc.ToXaml();
        AssertWellFormed(saved);
        Assert.Contains("d:DesignWidth=\"500\"", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet/2021/maui/design", saved, StringComparison.Ordinal);
        undo.Undo();
        Assert.Equal(text, doc.ToXaml());
    }

    [Fact]
    public void Rename_InAMauiFileWith2006XamlNamespace_EditsTheExistingName()
    {
        var text = $"""<ContentPage xmlns="{Maui}" xmlns:x="{FrameworkProfile.Xaml2006Namespace}" x:Class="App.P"><Grid x:Name="g" /></ContentPage>""";
        var doc = XamlDocument.Parse(text);
        doc.Profile = FrameworkProfile.Maui11;
        Assert.Equal(FrameworkProfile.Xaml2006Namespace, doc.XamlNamespace.NamespaceName);
        var grid = doc.AllNodes.Single(n => n.TypeName == "Grid");
        Assert.Null(new DesignerActions(doc, new UndoManager(doc)).Rename(grid.Id, "main"));

        var saved = doc.ToXaml();
        AssertWellFormed(saved);
        Assert.Contains("<Grid x:Name=\"main\" />", saved, StringComparison.Ordinal);
        Assert.Single(XDocument.Parse(saved).Root!.Attributes(), a => a.IsNamespaceDeclaration && a.Name.LocalName == "x");
    }

    [Fact]
    public void Rename_WhenXIsBoundToAnotherNamespace_DeclaresAFreePrefix()
    {
        var text = $"""<UserControl xmlns="{Avalonia}" xmlns:x="using:Other"><Grid /></UserControl>""";
        var doc = XamlDocument.Parse(text);
        var grid = doc.AllNodes.Single(n => n.TypeName == "Grid");
        Assert.Null(new DesignerActions(doc, new UndoManager(doc)).Rename(grid.Id, "main"));

        var saved = XDocument.Parse(doc.ToXaml());
        Assert.Equal("using:Other", saved.Root!.GetNamespaceOfPrefix("x")!.NamespaceName);
        Assert.Equal("main", (string?)saved.Root.Elements().Single().Attribute(XName.Get("Name", FrameworkProfile.Xaml2006Namespace)));
    }

    [Fact]
    public void FreePrefix_CountsImplicitPrefixes()
    {
        var doc = XamlDocument.Parse("<ContentPage x:Class=\"App.P\"><Grid /></ContentPage>");
        Assert.Equal("x2", doc.Xml.Root!.FreePrefix("x"));
        Assert.Equal("local", doc.Xml.Root!.FreePrefix("local"));
    }
}
