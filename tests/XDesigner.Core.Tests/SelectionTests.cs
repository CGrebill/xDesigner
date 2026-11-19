using XDesigner.Core.Editing;
using XDesigner.Core.Rendering;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Rendering;

namespace XDesigner.Core.Tests;

public class SelectionTests
{
    private static ElementLayout L(int id, double x, double y, double w, double h, bool visible = true, params ElementLayout[] children) =>
        new(id, "T", new RectDto(x, y, w, h), visible, ThicknessDto.Zero, ThicknessDto.Zero, null, children);

    private static readonly LayoutIndex Layout = new(
        L(1, 0, 0, 400, 300, true,
            L(2, 10, 10, 200, 200, true,
                L(3, 20, 20, 50, 50),
                L(4, 40, 40, 50, 50)),           // overlaps 3, later sibling → on top
            L(5, 300, 10, 50, 50, false)));      // invisible

    [Theory]
    [InlineData(25, 25, 3)]   // only in 3
    [InlineData(45, 45, 4)]   // overlap: later sibling wins
    [InlineData(150, 150, 2)] // inside the panel, over no child
    [InlineData(320, 20, 1)]  // over an invisible element → its parent
    [InlineData(390, 290, 1)] // root background
    public void HitTest_FindsDeepestTopmostVisible(double x, double y, int expected)
    {
        Assert.Equal(expected, Layout.HitTest(x, y)!.DesignId);
    }

    [Fact]
    public void HitTest_OutsideFrame_IsNull()
    {
        Assert.Null(Layout.HitTest(500, 10));
    }

    [Fact]
    public void HitTest_RespectsSelectableFilter()
    {
        Assert.Equal(2, Layout.HitTest(25, 25, e => e.DesignId != 3)!.DesignId);
    }

    [Fact]
    public void Index_KnowsParents()
    {
        Assert.Equal(2, Layout.ParentOf(4));
        Assert.Null(Layout.ParentOf(1));
        Assert.Equal(new RectDto(40, 40, 50, 50), Layout.Find(4)!.Bounds);
    }

    [Fact]
    public void Selection_ClickCtrlShiftSemantics()
    {
        var selection = new SelectionService();
        var changes = 0;
        selection.Changed += (_, _) => changes++;

        selection.Select(3);
        selection.Toggle(4);
        Assert.Equal([3, 4], selection.Selected);
        Assert.Equal(4, selection.Primary);

        selection.Add(3);           // re-adding makes it primary
        Assert.Equal([4, 3], selection.Selected);

        selection.Toggle(4);
        Assert.Equal([3], selection.Selected);

        selection.Select(3);        // no-op: no event
        Assert.Equal(4, changes);

        selection.RemoveMissing(id => id != 3);
        Assert.Empty(selection.Selected);
        Assert.Null(selection.Primary);
    }

    [Fact]
    public void Navigator_ParentAndSiblings()
    {
        var doc = XamlDocument.Parse("""
            <StackPanel xmlns="https://github.com/avaloniaui"><Button /><TextBox /><CheckBox /></StackPanel>
            """);
        var selection = new SelectionService();
        var kids = doc.Root.Children.Select(c => c.Id).ToArray();

        Assert.Equal(doc.Root.Id, SelectionNavigator.Sibling(doc, selection, forward: true)); // nothing selected → root

        selection.Select(kids[2]);
        Assert.Equal(kids[0], SelectionNavigator.Sibling(doc, selection, forward: true));    // wraps
        Assert.Equal(kids[1], SelectionNavigator.Sibling(doc, selection, forward: false));
        Assert.Equal(doc.Root.Id, SelectionNavigator.Parent(doc, selection));

        selection.Select(doc.Root.Id);
        Assert.Null(SelectionNavigator.Parent(doc, selection));
        Assert.Equal(kids[0], SelectionNavigator.Sibling(doc, selection, forward: true));    // root → first child
    }

    [Fact]
    public void Navigator_InContentTheOutlineDoesNotList_StaysPut()
    {
        var doc = XamlDocument.Parse("""
            <TabControl xmlns="https://github.com/avaloniaui"><TabItem><TabItem.Header><TextBlock /></TabItem.Header></TabItem></TabControl>
            """);
        var header = doc.AllNodes.Single(n => n.TypeName == "TextBlock").Id;
        var selection = new SelectionService();
        selection.Select(header);

        Assert.Equal(header, SelectionNavigator.Sibling(doc, selection, forward: true));
    }
}
