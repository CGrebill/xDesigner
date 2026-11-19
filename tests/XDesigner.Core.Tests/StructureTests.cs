using XDesigner.Core.Editing;
using XDesigner.Core.Metadata;
using XDesigner.Core.Rendering;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;

namespace XDesigner.Core.Tests;

public class StructureTests
{
    private const string Av = ControlTypeInfo.AvaloniaXmlNamespace;

    private static ControlTypeInfo T(string name, ContentModel content, params string[] bases) =>
        new($"Avalonia.Controls.{name}", name, Av, "Avalonia.Controls", "Avalonia.Controls", true, true, content,
            content switch { ContentModel.Children => "Children", ContentModel.SingleChild => "Child", ContentModel.SingleContent => "Content", ContentModel.Items => "Items", _ => null },
            null, bases);

    private static readonly ControlCatalog Catalog = new([
        T("Window", ContentModel.SingleContent), T("Grid", ContentModel.Children), T("StackPanel", ContentModel.Children),
        T("Canvas", ContentModel.Children), T("Border", ContentModel.SingleChild), T("Button", ContentModel.SingleContent),
        T("TextBlock", ContentModel.Inlines), T("ListBox", ContentModel.Items, "Avalonia.Controls.Primitives.SelectingItemsControl"),
        T("ListBoxItem", ContentModel.SingleContent), T("TabControl", ContentModel.Items, "Avalonia.Controls.Primitives.SelectingItemsControl"),
        T("TabItem", ContentModel.SingleContent, "Avalonia.Controls.HeaderedContentControl"), T("Menu", ContentModel.Items),
        T("MenuItem", ContentModel.Items),
    ]);

    private const string Source =
        "<Window xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
        "  <Grid RowDefinitions=\"Auto,*,40\" ColumnDefinitions=\"*,*\">\n" +
        "    <TextBlock x:Name=\"title\" Text=\"Title\" Grid.ColumnSpan=\"2\" />\n" +
        "    <StackPanel x:Name=\"stack\" Grid.Row=\"1\">\n" +
        "      <Button x:Name=\"a\" Content=\"A\" />\n" +
        "      <Button x:Name=\"b\" Content=\"B\" />\n" +
        "    </StackPanel>\n" +
        "    <Button x:Name=\"ok\" Content=\"OK\" Grid.Row=\"2\" Grid.Column=\"1\" Margin=\"8\" HorizontalAlignment=\"Right\" />\n" +
        "    <ListBox x:Name=\"list\" Grid.Row=\"1\" Grid.Column=\"1\">\n" +
        "      <ListBoxItem Content=\"One\" />\n" +
        "      <ListBoxItem>Two</ListBoxItem>\n" +
        "    </ListBox>\n" +
        "  </Grid>\n" +
        "</Window>\n";

    private static (XamlDocument Doc, UndoManager Undo, DesignerActions Actions) Open(string source = Source)
    {
        var doc = XamlDocument.Parse(source);
        var undo = new UndoManager(doc);
        return (doc, undo, new DesignerActions(doc, undo));
    }

    private static int Id(XamlDocument doc, string name) => doc.AllNodes.Single(n => n.Name == name).Id;

    [Fact]
    public void BringToFront_AndSendToBack_ReorderSiblings()
    {
        var (doc, undo, actions) = Open();

        Assert.True(actions.SendToBack(Id(doc, "b")));
        Assert.Matches("x:Name=\"b\"[^\\n]*\\n\\s*<Button x:Name=\"a\"", doc.ToXaml());
        Assert.True(actions.BringToFront(Id(doc, "title")));
        Assert.EndsWith("<TextBlock x:Name=\"title\" Text=\"Title\" Grid.ColumnSpan=\"2\" />\n  </Grid>\n</Window>\n", doc.ToXaml());
        Assert.False(actions.BringToFront(Id(doc, "title"))); // already last

        undo.Undo();
        undo.Undo();
        Assert.Equal(Source, doc.ToXaml());
    }

    [Fact]
    public void MoveToContainer_Reparents_AndRefusesItsOwnChild()
    {
        var (doc, undo, actions) = Open();

        Assert.Null(actions.MoveToContainer(Id(doc, "ok"), Id(doc, "stack"), 1, LayoutIndex.Empty, Catalog));
        var text = doc.ToXaml();
        Assert.Matches("<Button x:Name=\"a\"[^\\n]*\\n\\s*<Button x:Name=\"ok\" Content=\"OK\"", text);
        Assert.DoesNotContain("Grid.Column=\"1\" Margin", text); // the Grid placement went with leaving the Grid

        Assert.NotNull(actions.MoveToContainer(Id(doc, "stack"), Id(doc, "a"), 0, LayoutIndex.Empty, Catalog)); // a Button can't hold more
        Assert.NotNull(actions.MoveToContainer(Id(doc, "stack"), Id(doc, "stack"), 0, LayoutIndex.Empty, Catalog));

        undo.Undo();
        Assert.Equal(Source, doc.ToXaml());
    }

    [Fact]
    public void WrapIn_MovesThePlacementToTheWrapper_AndUndoIsExact()
    {
        var (doc, undo, actions) = Open();

        var wrapper = actions.WrapIn([Id(doc, "ok")], Catalog.Find("Avalonia.Controls.Border")!);

        Assert.NotNull(wrapper);
        var text = doc.ToXaml().Replace("\r", "");
        Assert.Contains("<Border Grid.Row=\"2\" Grid.Column=\"1\" Margin=\"8\" HorizontalAlignment=\"Right\">\n      <Button x:Name=\"ok\" Content=\"OK\" />\n    </Border>", text);
        Assert.Equal(Id(doc, "ok"), doc.FindById(wrapper!.Value)!.Children.Single().Id); // same element, same id

        undo.Undo();
        Assert.Equal(Source, doc.ToXaml());
    }

    [Fact]
    public void WrapIn_SeveralSiblings_NeedsAPanel()
    {
        var (doc, _, actions) = Open();
        int[] both = [Id(doc, "b"), Id(doc, "a")];

        Assert.Null(actions.WrapIn(both, Catalog.Find("Avalonia.Controls.Border")!)); // one child only
        Assert.NotNull(actions.WrapIn(both, Catalog.Find("Avalonia.Controls.StackPanel")!));
        Assert.Matches("<StackPanel>\\s*<Button x:Name=\"a\"[^\\n]*\\n\\s*<Button x:Name=\"b\"", doc.ToXaml()); // document order kept
    }

    [Fact]
    public void GridDefinitions_ReadCompactAndElementForms()
    {
        var (doc, _, _) = Open();
        var rows = GridDefinitions.Read(doc.AllNodes.First(n => n.TypeName == "Grid").Element, rows: true)!;

        Assert.Equal(["Auto", "*", "40"], rows.Select(r => r.LengthText));
        Assert.Equal([GridUnit.Auto, GridUnit.Star, GridUnit.Pixel], rows.Select(r => r.Unit));

        var element = XamlDocument.Parse("<Grid xmlns=\"https://github.com/avaloniaui\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"2*\" MinWidth=\"100\" /><ColumnDefinition /></Grid.ColumnDefinitions></Grid>");
        var columns = GridDefinitions.Read(element.Root.Element, rows: false)!;
        Assert.Equal((GridUnit.Star, 2.0, (double?)100), (columns[0].Unit, columns[0].Value, columns[0].Min));
        Assert.Equal("*", columns[1].LengthText);
    }

    [Fact]
    public void SetGridDefinitions_DeletingARow_MovesItsChildren_AndUndoIsExact()
    {
        var (doc, undo, actions) = Open();
        var grid = doc.AllNodes.First(n => n.TypeName == "Grid").Id;

        // Delete the middle row (*), keep Auto and 40, add a new star row at the end; keep the columns.
        IReadOnlyList<GridDefinition> rows = [new(GridUnit.Auto, OriginalIndex: 0), new(GridUnit.Pixel, 40, OriginalIndex: 2), new(GridUnit.Star)];
        IReadOnlyList<GridDefinition> columns = [new(GridUnit.Star, OriginalIndex: 0), new(GridUnit.Star, OriginalIndex: 1)];
        Assert.True(actions.SetGridDefinitions(grid, rows, columns));

        var text = doc.ToXaml();
        Assert.Contains("RowDefinitions=\"Auto,40,*\" ColumnDefinitions=\"*,*\"", text);
        Assert.Contains("<StackPanel x:Name=\"stack\">", text);                             // its row was deleted: the first row (no attribute)
        Assert.Contains("<Button x:Name=\"ok\" Content=\"OK\" Grid.Row=\"1\" Grid.Column=\"1\"", text); // row 2 is now row 1

        undo.Undo();
        Assert.Equal(Source, doc.ToXaml());
    }

    [Fact]
    public void SetGridDefinitions_WithMinMax_WritesDefinitionElements()
    {
        var (doc, undo, actions) = Open();
        var grid = doc.AllNodes.First(n => n.TypeName == "Grid").Id;
        IReadOnlyList<GridDefinition> rows = [new(GridUnit.Auto, OriginalIndex: 0), new(GridUnit.Star, OriginalIndex: 1), new(GridUnit.Pixel, 40, OriginalIndex: 2)];
        IReadOnlyList<GridDefinition> columns = [new(GridUnit.Star, 1, Min: 120, OriginalIndex: 0), new(GridUnit.Star, OriginalIndex: 1)];

        actions.SetGridDefinitions(grid, rows, columns);

        var text = doc.ToXaml().Replace("\r", "");
        Assert.DoesNotContain("ColumnDefinitions=", text);
        Assert.Contains("    <Grid.ColumnDefinitions>\n      <ColumnDefinition Width=\"*\" MinWidth=\"120\" />\n      <ColumnDefinition Width=\"*\" />\n    </Grid.ColumnDefinitions>\n", text);
        Assert.Contains("RowDefinitions=\"Auto,*,40\"", text); // rows unchanged
        Assert.Equal(2, GridDefinitions.Read(doc.FindById(grid)!.Element, rows: false)!.Count);

        undo.Undo();
        Assert.Equal(Source, doc.ToXaml());
    }

    [Fact]
    public void Items_ReadEditReorderAddAndRemove_AsOneUndoStep()
    {
        var (doc, undo, actions) = Open();
        var list = doc.FindById(Id(doc, "list"))!;
        var kind = ItemsEditing.KindOf(Catalog.Resolve(list))!;
        Assert.Equal("ListBoxItem", kind.ItemType);

        var drafts = ItemsEditing.Read(list, kind);
        Assert.Equal(["One", "Two"], drafts.Select(d => d.Text));

        drafts[1].Text = "Second";            // text content edited in place
        drafts.Reverse();                     // Second, One
        drafts.Add(new ItemDraft(null, "Three"));
        drafts.RemoveAt(1);                   // drop One
        Assert.True(actions.SetItems(list.Id, drafts, kind));

        var text = doc.ToXaml().Replace("\r", "");
        Assert.Contains("<ListBox x:Name=\"list\" Grid.Row=\"1\" Grid.Column=\"1\">\n      <ListBoxItem>Second</ListBoxItem>\n      <ListBoxItem Content=\"Three\" />\n    </ListBox>", text);
        Assert.Equal("Edit items of list", undo.UndoDescription);

        undo.Undo();
        Assert.Equal(Source, doc.ToXaml());
    }

    [Fact]
    public void NewTabs_GetAGrid_AndMenusNest()
    {
        const string source = "<StackPanel xmlns=\"https://github.com/avaloniaui\">\n  <TabControl />\n  <Menu />\n</StackPanel>\n";
        var (doc, undo, actions) = Open(source);
        var tabs = doc.AllNodes.Single(n => n.TypeName == "TabControl");
        var menu = doc.AllNodes.Single(n => n.TypeName == "Menu");

        actions.SetItems(tabs.Id, [new ItemDraft(null, "General"), new ItemDraft(null, "Advanced")], ItemsEditing.KindOf(Catalog.Resolve(tabs))!);
        var file = new ItemDraft(null, "_File");
        file.Children.Add(new ItemDraft(null, "_Open"));
        file.Children.Add(new ItemDraft(null, "E_xit"));
        actions.SetItems(menu.Id, [file], ItemsEditing.KindOf(Catalog.Resolve(menu))!);

        var text = doc.ToXaml().Replace("\r", "");
        Assert.Contains("<TabControl>\n    <TabItem Header=\"General\">\n      <Grid />\n    </TabItem>\n    <TabItem Header=\"Advanced\">\n      <Grid />\n    </TabItem>\n  </TabControl>", text);
        Assert.Contains("<Menu>\n    <MenuItem Header=\"_File\">\n      <MenuItem Header=\"_Open\" />\n      <MenuItem Header=\"E_xit\" />\n    </MenuItem>\n  </Menu>", text);

        var read = ItemsEditing.Read(doc.FindById(menu.Id)!, ItemsEditing.KindOf(Catalog.Resolve(menu))!);
        Assert.Equal(["_Open", "E_xit"], read.Single().Children.Select(c => c.Text));

        undo.Undo();
        undo.Undo();
        Assert.Equal(source, doc.ToXaml());
    }

    [Fact]
    public void DesignPreview_ShowsTheTabTheSelectionIsIn_WithoutChangingTheFile()
    {
        const string source = "<TabControl xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "  <TabItem Header=\"A\"><Grid /></TabItem>\n  <TabItem Header=\"B\"><Grid><Button x:Name=\"go\" /></Grid></TabItem>\n</TabControl>\n";
        var doc = XamlDocument.Parse(source);
        var preview = new DesignPreview();

        Assert.True(preview.Follow(doc, Catalog, [Id(doc, "go")]));
        Assert.False(preview.Follow(doc, Catalog, [Id(doc, "go")]));
        var copy = doc.BuildRenderCopy(preview.Overrides(doc, Catalog));

        Assert.Contains("<TabControl SelectedIndex=\"1\"", copy.Replace(" xmlns=\"https://github.com/avaloniaui\"", "").Replace(" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"", "").Replace(" xmlns:dsn=\"" + Protocol.ProtocolInfo.DesignIdXmlNamespace + "\"", "").Replace(" dsn:DesignId.Value=\"1\"", ""));
        Assert.Equal(source, doc.ToXaml());
    }
}
