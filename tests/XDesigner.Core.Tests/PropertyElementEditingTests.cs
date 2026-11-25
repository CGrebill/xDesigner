using System.Xml.Linq;
using XDesigner.Core.Editing;
using XDesigner.Core.Layout;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;

namespace XDesigner.Core.Tests;

/// <summary>Containers whose content is written as a property element (<c>&lt;StackPanel.Children&gt;</c>, <c>&lt;Border.Child&gt;</c>).</summary>
public class PropertyElementEditingTests
{
    private const string Av = "https://github.com/avaloniaui";

    /// <summary>A new child goes inside the content property element, at the planned index (review M14).</summary>
    [Fact]
    public void Insert_GoesIntoTheContentPropertyElement_AtTheIndex()
    {
        var text = $"""
            <UserControl xmlns="{Av}">
              <StackPanel>
                <StackPanel.Children>
                  <TextBlock Text="A" />
                  <TextBlock Text="B" />
                </StackPanel.Children>
              </StackPanel>
            </UserControl>
            """.ReplaceLineEndings("\n");
        var doc = XamlDocument.Parse(text);
        var undo = new UndoManager(doc);
        var panel = doc.Root.Children.Single();

        undo.Execute(new InsertElementCommand(panel.Id, new XElement(XName.Get("Button", Av)), 1, "Add"));

        Assert.Equal(["A", "Button", "B"], panel.Children.Select(c => (string?)c.Element.Attribute("Text") ?? c.TypeName));
        Assert.Empty(panel.Element.Elements(XName.Get("Button", Av))); // nothing written after </StackPanel.Children>
        undo.Undo();
        Assert.Equal(text, doc.ToXaml());
    }

    /// <summary>Deleting a Border's only child removes the emptied &lt;Border.Child&gt;, and something can be dropped back (review L2).</summary>
    [Fact]
    public void DeletingTheOnlyChild_RemovesTheSingleValuePropertyElement()
    {
        var text = $"""
            <UserControl xmlns="{Av}">
              <Border>
                <Border.Child>
                  <Button />
                </Border.Child>
              </Border>
            </UserControl>
            """.ReplaceLineEndings("\n");
        var doc = XamlDocument.Parse(text);
        var undo = new UndoManager(doc);
        var border = doc.Root.Children.Single();
        var type = new ControlTypeInfo("Avalonia.Controls.Border", "Border", Av, "Avalonia.Controls", "Avalonia.Controls", true, true, ContentModel.SingleChild, "Child", null, []);
        Assert.False(DropPlanner.Accepts(border, type));

        undo.Execute(new RemoveElementCommand(border.Children.Single().Id));

        Assert.DoesNotContain("Border.Child", doc.ToXaml(), StringComparison.Ordinal);
        Assert.True(DropPlanner.Accepts(border, type));
        undo.Undo();
        Assert.Equal(text, doc.ToXaml());
    }

    [Fact]
    public void AnEmptyContentPropertyElement_DoesNotMakeTheElementFull()
    {
        var doc = XamlDocument.Parse($"""<UserControl xmlns="{Av}"><Border><Border.Child></Border.Child></Border></UserControl>""");
        var border = doc.Root.Children.Single();
        var type = new ControlTypeInfo("Avalonia.Controls.Border", "Border", Av, "Avalonia.Controls", "Avalonia.Controls", true, true, ContentModel.SingleChild, "Child", null, []);

        Assert.True(DropPlanner.Accepts(border, type));
    }
}
