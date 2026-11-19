using System.Xml.Linq;
using XDesigner.Core.Editing;
using XDesigner.Core.Layout;
using XDesigner.Core.Metadata;
using XDesigner.Core.Naming;
using XDesigner.Core.Rendering;
using XDesigner.Core.Toolbox;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;
using XDesigner.Protocol.Rendering;

namespace XDesigner.Core.Tests;

public class DropAndAddTests
{
    private const string Av = ControlTypeInfo.AvaloniaXmlNamespace;

    private static ControlTypeInfo T(string name, ContentModel content, string ns = "Avalonia.Controls", string? assembly = null, params string[] bases) =>
        new($"{ns}.{name}", name, assembly is null ? Av : $"using:{ns}", ns, assembly ?? "Avalonia.Controls", assembly is null, true, content,
            content is ContentModel.Children ? "Children" : content is ContentModel.SingleChild ? "Child" : content is ContentModel.SingleContent ? "Content" : null,
            null, bases);

    private static readonly ControlCatalog Catalog = new([
        T("Window", ContentModel.SingleContent, bases: ["Avalonia.Controls.TopLevel"]),
        T("Grid", ContentModel.Children), T("StackPanel", ContentModel.Children), T("Canvas", ContentModel.Children),
        T("DockPanel", ContentModel.Children), T("WrapPanel", ContentModel.Children), T("Panel", ContentModel.Children),
        T("Border", ContentModel.SingleChild), T("Button", ContentModel.SingleContent), T("TextBlock", ContentModel.Inlines),
        T("ListBox", ContentModel.Items),
        T("ContactCard", ContentModel.None, "My.App.Controls", "My.App", "Avalonia.Controls.UserControl"),
        T("StarRating", ContentModel.None, "My.App.Controls", "My.App"),
    ]);

    /// <summary>The test form with \n line endings, like the expected snippets, whatever line endings the checkout uses.</summary>
    private static readonly string Form = FormSource.ReplaceLineEndings("\n");

    private const string FormSource = """
        <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Grid ColumnDefinitions="100,*" RowDefinitions="50,*">
            <Canvas Grid.Row="1" />
            <StackPanel Grid.Column="1">
              <Button x:Name="button1" Content="A" />
              <Button x:Name="button2" Content="B" />
            </StackPanel>
            <DockPanel Grid.Row="1" Grid.Column="1">
              <TextBlock Text="fill" />
            </DockPanel>
            <Border />
          </Grid>
        </Window>
        """;

    /// <summary>Hand-made layout matching <see cref="Form"/>: window 400×300, grid at (10,10) 380×280.</summary>
    private static (XamlDocument Doc, LayoutIndex Layout, Dictionary<string, int> Ids) Setup()
    {
        var doc = XamlDocument.Parse(Form);
        var ids = new Dictionary<string, int>();
        foreach (var node in doc.AllNodes)
        {
            ids[node.Name ?? node.TypeName] = node.Id;
        }

        ElementLayout L(string key, double x, double y, double w, double h, PanelInfo? panel = null, params ElementLayout[] children) =>
            new(ids[key], key, new RectDto(x, y, w, h), true, ThicknessDto.Zero, ThicknessDto.Zero, panel, children);

        var layout = new LayoutIndex(
            L("Window", 0, 0, 400, 300, null,
                L("Grid", 10, 10, 380, 280, new PanelInfo(PanelKind.Grid, RowOffsets: [0, 50, 280], ColumnOffsets: [0, 100, 380]),
                    L("Canvas", 10, 60, 100, 230, new PanelInfo(PanelKind.Canvas)),
                    L("StackPanel", 110, 10, 280, 50, new PanelInfo(PanelKind.StackPanel, LayoutOrientation.Vertical),
                        L("button1", 110, 10, 280, 25),
                        L("button2", 110, 35, 280, 25)),
                    L("DockPanel", 110, 60, 280, 230, new PanelInfo(PanelKind.DockPanel),
                        L("TextBlock", 110, 60, 280, 230)),
                    L("Border", 10, 10, 100, 50))));
        return (doc, layout, ids);
    }

    private static DropPlan Plan(double x, double y)
    {
        var (doc, layout, _) = Setup();
        return DropPlanner.Plan(doc, Catalog, layout, x, y, out var reason) ?? throw new Xunit.Sdk.XunitException(reason);
    }

    private static string? Attr(DropPlan plan, string name) => plan.Attributes.FirstOrDefault(a => a.Key == name).Value;

    [Fact]
    public void Canvas_PositionsAtThePoint()
    {
        var (_, _, ids) = Setup();
        var plan = Plan(40.4, 100.6);

        Assert.Equal(ids["Canvas"], plan.ContainerId);
        Assert.Equal("30", Attr(plan, "Canvas.Left"));
        Assert.Equal("41", Attr(plan, "Canvas.Top"));
        Assert.True(plan.IsPositioned);
    }

    [Fact]
    public void Grid_PicksCellAndMarginWithinIt()
    {
        var (doc, layout, ids) = Setup();
        // Over the Border (row 0, col 0) the Border is empty and takes the drop; force the Grid by excluding it.
        var plan = DropPlanner.Plan(doc, Catalog, layout, 30, 40, out _, exclude: new HashSet<int> { ids["Border"] })!;

        Assert.Equal(ids["Grid"], plan.ContainerId);
        Assert.Null(Attr(plan, "Grid.Row"));
        Assert.Null(Attr(plan, "Grid.Column"));
        Assert.Equal("Left", Attr(plan, "HorizontalAlignment"));
        Assert.Equal("Top", Attr(plan, "VerticalAlignment"));
        Assert.Equal("20,30,0,0", Attr(plan, "Margin"));
        Assert.Equal(new RectDto(10, 10, 100, 50), plan.Indicator.Area);
    }

    [Fact]
    public void StackPanel_InsertsBetweenChildren()
    {
        var (_, _, ids) = Setup();

        var beforeSecond = Plan(200, 30);   // below button1's middle (22.5), above button2's (47.5)
        var atEnd = Plan(200, 55);

        Assert.Equal(ids["StackPanel"], beforeSecond.ContainerId);
        Assert.Equal(1, beforeSecond.Index);
        Assert.Equal(DropIndicatorKind.InsertionLine, beforeSecond.Indicator.Kind);
        Assert.Equal(new RectDto(110, 34, 280, 2), beforeSecond.Indicator.Line);
        Assert.Equal(2, atEnd.Index);
        Assert.Empty(beforeSecond.Attributes);
    }

    [Fact]
    public void ButtonWithContent_PassesTheDropToItsParent()
    {
        var (_, _, ids) = Setup();

        Assert.Equal(ids["StackPanel"], Plan(150, 20).ContainerId); // over button1
    }

    [Theory]
    [InlineData(120, 150, "Left", 0)]   // near left edge → docked, before the filling TextBlock
    [InlineData(380, 150, "Right", 0)]
    [InlineData(250, 65, "Top", 0)]
    [InlineData(250, 285, "Bottom", 0)]
    public void DockPanel_DocksToNearestEdge(double x, double y, string edge, int index)
    {
        var plan = Plan(x, y);

        Assert.Equal(edge, Attr(plan, "DockPanel.Dock"));
        Assert.Equal(index, plan.Index);
        Assert.Equal(DropIndicatorKind.DockArea, plan.Indicator.Kind);
    }

    [Fact]
    public void DockPanel_CentreBecomesLastChild()
    {
        var plan = Plan(250, 175);

        Assert.Null(Attr(plan, "DockPanel.Dock"));
        Assert.Equal(1, plan.Index);
    }

    [Fact]
    public void EmptyBorder_TakesTheChild()
    {
        var (_, _, ids) = Setup();
        var plan = Plan(30, 30);

        Assert.Equal(ids["Border"], plan.ContainerId);
        Assert.Equal(DropIndicatorKind.Container, plan.Indicator.Kind);
        Assert.False(plan.IsPositioned);
    }

    [Fact]
    public void OutsideTheForm_HasNoPlan()
    {
        var (doc, layout, _) = Setup();

        Assert.Null(DropPlanner.Plan(doc, Catalog, layout, 500, 500, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void WindowMarginArea_WithContent_Rejects()
    {
        var (doc, layout, _) = Setup();

        // (5,5) is inside the window but outside the grid; the window already has content.
        Assert.Null(DropPlanner.Plan(doc, Catalog, layout, 5, 5, out var reason));
        Assert.Contains("panel", reason);
    }

    [Fact]
    public void AddControl_ToCanvas_WritesNamePositionAndDefaults()
    {
        var (doc, layout, _) = Setup();
        var undo = new UndoManager(doc);
        var actions = new DesignerActions(doc, undo);
        var item = new ToolboxItem(Catalog.Find("Avalonia.Controls.Button")!, "Common", "", new Dictionary<string, string> { ["Content"] = "{name}" }, new Dictionary<string, string>());

        var id = actions.AddControl(item, DropPlanner.Plan(doc, Catalog, layout, 40, 100, out _)!);

        var element = doc.FindById(id)!.Element;
        Assert.Equal("button3", doc.FindById(id)!.Name);   // button1/button2 are taken
        Assert.Equal(
            ["{http://schemas.microsoft.com/winfx/2006/xaml}Name", "Canvas.Left", "Canvas.Top", "Content"],
            element.Attributes().Select(a => a.Name.ToString()));
        Assert.Equal("button3", (string?)element.Attribute("Content"));
        Assert.Contains("<Canvas Grid.Row=\"1\">\n      <Button x:Name=\"button3\" Canvas.Left=\"30\" Canvas.Top=\"40\" Content=\"button3\" />\n    </Canvas>", doc.ToXaml());

        undo.Undo();
        Assert.Equal(Form, doc.ToXaml());
    }

    [Fact]
    public void AddPanel_AtAPoint_GetsDefaultSize_ButNotInAStack()
    {
        var (doc, layout, _) = Setup();
        var actions = new DesignerActions(doc, new UndoManager(doc));
        var grid = new ToolboxItem(Catalog.Find("Avalonia.Controls.Grid")!, "Layout", "", new Dictionary<string, string>(),
            new Dictionary<string, string> { ["Width"] = "200", ["Height"] = "150" });

        var onCanvas = doc.FindById(actions.AddControl(grid, DropPlanner.Plan(doc, Catalog, layout, 40, 100, out _)!))!.Element;
        var inStack = doc.FindById(actions.AddControl(grid, DropPlanner.Plan(doc, Catalog, layout, 200, 30, out _)!))!.Element;

        Assert.Equal("200", (string?)onCanvas.Attribute("Width"));
        Assert.Null(inStack.Attribute("Width"));
        Assert.Equal(["grid1", "grid2"], new[] { onCanvas, inStack }.Select(e => (string?)e.Attribute(XName.Get("Name", XamlNamespaces.Xaml))));
    }

    [Fact]
    public void AddCustomControl_DeclaresNamespaceOnce()
    {
        var (doc, layout, _) = Setup();
        var undo = new UndoManager(doc);
        var actions = new DesignerActions(doc, undo);
        var stars = ToolboxCatalog.ItemFor(Catalog.Find("My.App.Controls.StarRating")!);

        actions.AddControl(stars, DropPlanner.Plan(doc, Catalog, layout, 40, 100, out _)!);
        actions.AddControl(stars, DropPlanner.Plan(doc, Catalog, layout, 60, 120, out _)!);

        var text = doc.ToXaml();
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "xmlns:local=\"using:My.App.Controls\""));
        Assert.Contains("<local:StarRating x:Name=\"starRating1\"", text);
        Assert.Contains("<local:StarRating x:Name=\"starRating2\"", text);

        undo.Undo();
        undo.Undo();
        Assert.Equal(Form, doc.ToXaml());
    }

    [Fact]
    public void ExistingClrNamespaceDeclaration_IsReused()
    {
        var root = XElement.Parse("<Window xmlns=\"https://github.com/avaloniaui\" xmlns:c=\"clr-namespace:My.App.Controls;assembly=My.App\" />");

        var ns = DesignerActions.ElementNamespace(root, "using:My.App.Controls", "My.App.Controls", out var declaration);

        Assert.Equal("clr-namespace:My.App.Controls;assembly=My.App", ns.NamespaceName);
        Assert.Null(declaration);
    }

    [Fact]
    public void Delete_SkipsRootAndNestedDuplicates_AndUndoes()
    {
        var (doc, _, ids) = Setup();
        var undo = new UndoManager(doc);
        var actions = new DesignerActions(doc, undo);

        var deleted = actions.Delete([doc.Root.Id, ids["StackPanel"], ids["button1"], ids["Border"]]);

        Assert.Equal(2, deleted);   // StackPanel (with button1 inside) and Border
        Assert.Equal("Delete 2 controls", undo.UndoDescription);
        Assert.DoesNotContain("StackPanel", doc.ToXaml());
        undo.Undo();
        Assert.Equal(Form, doc.ToXaml());
    }

    [Theory]
    [InlineData("Button", "button")]
    [InlineData("TextBox", "textBox")]
    [InlineData("UIElement", "uiElement")]
    [InlineData("X", "x")]
    public void CamelCase(string type, string expected) => Assert.Equal(expected, NameGenerator.CamelCase(type));

    [Fact]
    public void NameValidation()
    {
        var (doc, _, ids) = Setup();

        Assert.True(NameGenerator.IsValid(doc, "okButton", out _));
        Assert.False(NameGenerator.IsValid(doc, "class", out var keyword));
        Assert.Contains("keyword", keyword);
        Assert.False(NameGenerator.IsValid(doc, "2fast", out _));
        Assert.False(NameGenerator.IsValid(doc, "button2", out _));
        Assert.True(NameGenerator.IsValid(doc, "button2", out _, exceptNodeId: ids["button2"]));
    }

    [Fact]
    public void Toolbox_CuratedCategoriesThenProjectAndOther()
    {
        var categories = ToolboxCatalog.Build(Catalog, userAssemblyName: "My.App");

        var common = categories.Single(c => c.Name == "Common");
        Assert.Equal(["Button", "TextBlock", "ListBox"], common.Items.Select(i => i.DisplayName));
        Assert.Equal("{name}", common.Items[0].Attributes["Content"]);
        Assert.Equal(["ContactCard", "StarRating"], categories.Single(c => c.Name == "Project").Items.Select(i => i.DisplayName));
        Assert.DoesNotContain(categories, c => c.Name == "Other"); // everything built-in here is curated…
        Assert.DoesNotContain(categories.SelectMany(c => c.Items), i => i.DisplayName == "Window"); // …and windows can't be dropped
    }
}
