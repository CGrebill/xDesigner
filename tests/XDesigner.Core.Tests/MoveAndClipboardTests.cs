using System.Xml.Linq;
using XDesigner.Core.Editing;
using XDesigner.Core.Layout;
using XDesigner.Core.Metadata;
using XDesigner.Core.Rendering;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;
using XDesigner.Protocol.Rendering;

namespace XDesigner.Core.Tests;

public class MoveAndClipboardTests
{
    private const string Av = ControlTypeInfo.AvaloniaXmlNamespace;

    private static ControlTypeInfo T(string name, ContentModel content, string ns = "Avalonia.Controls", string? assembly = null, string[]? events = null) =>
        new($"{ns}.{name}", name, assembly is null ? Av : $"using:{ns}", ns, assembly ?? "Avalonia.Controls", assembly is null, true, content,
            content switch { ContentModel.Children => "Children", ContentModel.SingleContent => "Content", _ => null }, null, [], events);

    private static readonly ControlCatalog Catalog = new([
        T("Window", ContentModel.SingleContent), T("Grid", ContentModel.Children), T("StackPanel", ContentModel.Children),
        T("Canvas", ContentModel.Children), T("Button", ContentModel.SingleContent, events: ["Click", "PointerPressed"]),
        T("StarRating", ContentModel.None, "My.Controls", "My"),
    ]);

    /// <summary>The test form with \n line endings, like the expected snippets, whatever line endings the checkout uses.</summary>
    private static readonly string Form = FormSource.ReplaceLineEndings("\n");

    private const string FormSource = """
        <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:c="using:My.Controls">
          <Grid ColumnDefinitions="100,*" RowDefinitions="50,*">
            <Canvas Grid.Row="1">
              <Button x:Name="onCanvas" Canvas.Left="10" Canvas.Top="20" Content="C" Click="onCanvas_Click" />
            </Canvas>
            <StackPanel Grid.Column="1">
              <Button x:Name="button1" Content="A" />
              <Button x:Name="button2" Content="B" />
            </StackPanel>
            <Button x:Name="inGrid" Grid.Row="1" Grid.Column="1" HorizontalAlignment="Left" VerticalAlignment="Top" Margin="30,40,0,0" Content="G" />
            <c:StarRating x:Name="stars" Grid.Row="1" />
          </Grid>
        </Window>
        """;

    private sealed record Setup(XamlDocument Doc, UndoManager Undo, DesignerActions Actions, LayoutIndex Layout, Dictionary<string, int> Ids)
    {
        public XElement El(string key) => Doc.FindById(Ids[key])!.Element;

        public string? A(string key, string attribute) => (string?)El(key).Attribute(attribute);
    }

    private static Setup Open()
    {
        var doc = XamlDocument.Parse(Form);
        var undo = new UndoManager(doc);
        var ids = doc.AllNodes.ToDictionary(n => n.Name ?? n.TypeName, n => n.Id);

        ElementLayout L(string key, double x, double y, double w, double h, PanelInfo? panel = null, params ElementLayout[] children) =>
            new(ids[key], key, new RectDto(x, y, w, h), true, ThicknessDto.Zero, ThicknessDto.Zero, panel, children);

        var layout = new LayoutIndex(
            L("Window", 0, 0, 400, 300, null,
                L("Grid", 10, 10, 380, 280, new PanelInfo(PanelKind.Grid, RowOffsets: [0, 50, 280], ColumnOffsets: [0, 100, 380]),
                    L("Canvas", 10, 60, 100, 230, new PanelInfo(PanelKind.Canvas),
                        L("onCanvas", 20, 80, 40, 25)),
                    L("StackPanel", 110, 10, 280, 50, new PanelInfo(PanelKind.StackPanel, LayoutOrientation.Vertical),
                        L("button1", 110, 10, 280, 25),
                        L("button2", 110, 35, 280, 25)),
                    L("inGrid", 140, 100, 50, 25),
                    L("stars", 10, 60, 120, 24))));
        return new Setup(doc, undo, new DesignerActions(doc, undo), layout, ids);
    }

    private static void UndoAllAndCheck(Setup s)
    {
        while (s.Undo.CanUndo)
        {
            s.Undo.Undo();
        }

        Assert.Equal(Form, s.Doc.ToXaml());
    }

    [Fact]
    public void MoveBy_OnCanvas_ShiftsLeftAndTop()
    {
        var s = Open();

        Assert.Equal(1, s.Actions.MoveBy([s.Ids["onCanvas"]], 5, 7, s.Layout, Catalog));

        Assert.Equal(("15", "27"), (s.A("onCanvas", "Canvas.Left"), s.A("onCanvas", "Canvas.Top")));
        Assert.Equal("Move onCanvas", s.Undo.UndoDescription);
        UndoAllAndCheck(s);
    }

    [Fact]
    public void MoveBy_InGrid_RecomputesCellAndMargin()
    {
        var s = Open();

        s.Actions.MoveBy([s.Ids["inGrid"]], 100, 0, s.Layout, Catalog);
        Assert.Equal("130,40,0,0", s.A("inGrid", "Margin"));
        Assert.Equal("1", s.A("inGrid", "Grid.Column"));

        s.Undo.Undo();
        s.Actions.MoveBy([s.Ids["inGrid"]], -100, -80, s.Layout, Catalog); // to (40,20) → row 0, column 0
        Assert.Null(s.A("inGrid", "Grid.Row"));
        Assert.Null(s.A("inGrid", "Grid.Column"));
        Assert.Equal("30,10,0,0", s.A("inGrid", "Margin"));
        UndoAllAndCheck(s);
    }

    [Fact]
    public void MoveBy_IgnoresElementsInStackPanels()
    {
        var s = Open();

        Assert.Equal(0, s.Actions.MoveBy([s.Ids["button1"]], 10, 10, s.Layout, Catalog));
        Assert.False(s.Undo.CanUndo);
    }

    [Fact]
    public void MoveTo_ReordersWithinStackPanel()
    {
        var s = Open();
        var exclude = s.Actions.WithDescendants([s.Ids["button1"]]);
        var plan = DropPlanner.Plan(s.Doc, Catalog, s.Layout, 200, 55, out _, exclude)!;

        Assert.True(s.Actions.MoveTo(s.Ids["button1"], plan, s.Layout));

        var order = s.El("StackPanel").Elements().Select(e => (string?)e.Attribute(XName.Get("Name", XamlNamespaces.Xaml)));
        Assert.Equal(["button2", "button1"], order);
        Assert.Contains("      <Button x:Name=\"button2\" Content=\"B\" />\n      <Button x:Name=\"button1\" Content=\"A\" />\n    </StackPanel>", s.Doc.ToXaml());
        UndoAllAndCheck(s);
    }

    [Fact]
    public void MoveTo_SamePlace_DoesNothing()
    {
        var s = Open();
        var plan = DropPlanner.Plan(s.Doc, Catalog, s.Layout, 200, 15, out _, s.Actions.WithDescendants([s.Ids["button1"]]))!;

        Assert.False(s.Actions.MoveTo(s.Ids["button1"], plan, s.Layout));
    }

    [Fact]
    public void MoveTo_FromCanvasIntoStackPanel_DropsCanvasPosition()
    {
        var s = Open();
        var id = s.Ids["onCanvas"];
        var plan = DropPlanner.Plan(s.Doc, Catalog, s.Layout, 200, 30, out _, s.Actions.WithDescendants([id]))!;

        Assert.True(s.Actions.MoveTo(id, plan, s.Layout));

        Assert.Equal(s.Ids["StackPanel"], s.Doc.FindById(id)!.Parent!.Id);
        Assert.Null(s.A("onCanvas", "Canvas.Left"));
        Assert.Null(s.A("onCanvas", "Canvas.Top"));
        Assert.Equal(1, s.El("StackPanel").Elements().ToList().IndexOf(s.El("onCanvas")));
        Assert.Equal(id, s.Doc.NodeFor(s.El("onCanvas")).Id); // same element, same id
        UndoAllAndCheck(s);
    }

    [Fact]
    public void MoveTo_FromCanvasIntoGridCell_PositionsAndPinsSize()
    {
        var s = Open();
        var id = s.Ids["onCanvas"];
        // Pointer over the Grid's empty top-left cell; the element's new top-left is (25, 15).
        var plan = DropPlanner.Plan(s.Doc, Catalog, s.Layout, 30, 30, out _, s.Actions.WithDescendants([id]), positionX: 25, positionY: 15)!;

        s.Actions.MoveTo(id, plan, s.Layout);

        Assert.Equal(s.Ids["Grid"], s.Doc.FindById(id)!.Parent!.Id);
        Assert.Equal(
            ["{http://schemas.microsoft.com/winfx/2006/xaml}Name", "Content", "Click", "Width", "Height", "HorizontalAlignment", "VerticalAlignment", "Margin"],
            s.El("onCanvas").Attributes().Select(a => a.Name.ToString()));
        Assert.Equal("15,5,0,0", s.A("onCanvas", "Margin"));
        Assert.Equal(("40", "25"), (s.A("onCanvas", "Width"), s.A("onCanvas", "Height")));
        UndoAllAndCheck(s);
    }

    [Fact]
    public void MoveTo_IntoItself_IsRefused()
    {
        var s = Open();
        var plan = new DropPlan(s.Ids["StackPanel"], 0, [], false, new DropIndicator(DropIndicatorKind.InsertionLine, new RectDto(0, 0, 1, 1)));

        Assert.False(s.Actions.MoveTo(s.Ids["StackPanel"], plan, s.Layout));
    }

    [Fact]
    public void Resize_RightEdgeSetsWidth_LeftEdgeOnCanvasAlsoMoves()
    {
        var s = Open();

        s.Actions.Resize(s.Ids["inGrid"], new RectDto(140, 100, 80.4, 25), ResizeEdges.Right, s.Layout, Catalog);
        Assert.Equal("80", s.A("inGrid", "Width"));
        Assert.Null(s.A("inGrid", "Height"));
        Assert.Equal("30,40,0,0", s.A("inGrid", "Margin"));

        s.Actions.Resize(s.Ids["onCanvas"], new RectDto(14, 74, 46, 31), ResizeEdges.Left | ResizeEdges.Top, s.Layout, Catalog);
        Assert.Equal(("46", "31"), (s.A("onCanvas", "Width"), s.A("onCanvas", "Height")));
        Assert.Equal(("4", "14"), (s.A("onCanvas", "Canvas.Left"), s.A("onCanvas", "Canvas.Top")));
        UndoAllAndCheck(s);
    }

    [Fact]
    public void Nudge_PositionsMove_StackItemsReorder()
    {
        var s = Open();

        s.Actions.Nudge([s.Ids["onCanvas"]], 1, 0, s.Layout, Catalog);
        Assert.Equal("11", s.A("onCanvas", "Canvas.Left"));

        s.Actions.Nudge([s.Ids["button2"]], 0, -1, s.Layout, Catalog);
        Assert.Equal("button2", (string?)s.El("StackPanel").Elements().First().Attribute(XName.Get("Name", XamlNamespaces.Xaml)));

        Assert.False(s.Actions.Nudge([s.Ids["button2"]], 0, -1, s.Layout, Catalog)); // already first
        UndoAllAndCheck(s);
    }

    [Fact]
    public void Snap_MoveAlignsToSiblingEdge_WithGuide()
    {
        var sibling = new RectDto(100, 100, 50, 20);
        // Left edges 1 apart (centres 4 apart), so left-edge alignment wins.
        var result = SnapEngine.Snap(new RectDto(101, 200, 40, 20), ResizeEdges.None, [sibling], new RectDto(0, 0, 400, 300), threshold: 6);

        Assert.Equal(100, result.Bounds.X);
        Assert.Equal(200, result.Bounds.Y); // nothing near in Y
        var line = Assert.Single(result.Lines);
        Assert.Equal((100d, 100d, 100d, 220d), (line.X1, line.Y1, line.X2, line.Y2));
        Assert.False(line.IsSpacing);
    }

    [Fact]
    public void Snap_SpacingBelowSibling_AndContainerEdge()
    {
        var sibling = new RectDto(100, 100, 50, 20);
        var result = SnapEngine.Snap(new RectDto(4, 126, 40, 20), ResizeEdges.None, [sibling], new RectDto(0, 0, 400, 300), threshold: 6);

        Assert.Equal(0, result.Bounds.X);   // container left edge (4 away) beats the 8-DIP spacing line (4 away, listed later)
        Assert.Equal(128, result.Bounds.Y); // 8 below the sibling's bottom (120)
        Assert.Contains(result.Lines, l => l.IsSpacing && l.Y1 == 128);
    }

    [Fact]
    public void Snap_ResizeMovesOnlyTheDraggedEdge()
    {
        var result = SnapEngine.Snap(new RectDto(10, 10, 386, 50), ResizeEdges.Right, [], new RectDto(0, 0, 400, 300), threshold: 6);

        Assert.Equal(new RectDto(10, 10, 390, 50), result.Bounds); // right edge to 400; left edge untouched
    }

    [Fact]
    public void Snap_NothingWithinThreshold_LeavesBoundsAlone()
    {
        var input = new RectDto(50, 60, 40, 20);
        var result = SnapEngine.Snap(input, ResizeEdges.None, [], new RectDto(0, 0, 400, 300), threshold: 3);

        Assert.Equal(input, result.Bounds);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public void CopyPaste_SameDocument_RenamesOffsetsKeepsEvents()
    {
        var s = Open();
        var text = s.Actions.Copy([s.Ids["inGrid"], s.Ids["onCanvas"]])!;

        var pasted = s.Actions.Paste(text, s.Ids["inGrid"], s.Layout, Catalog, out var problem)!;

        Assert.Null(problem);
        Assert.Equal(2, pasted.Count);
        var nodes = pasted.Select(id => s.Doc.FindById(id)!).ToList();
        Assert.All(nodes, n => Assert.Equal(s.Ids["Grid"], n.Parent!.Id)); // inGrid can't hold children → its Grid
        Assert.Equal(["button3", "button4"], nodes.Select(n => n.Name));
        Assert.Equal("40,50,0,0", (string?)nodes.Single(n => n.Element.Attribute("Margin") is not null).Element.Attribute("Margin"));
        var fromCanvas = nodes.Single(n => (string?)n.Element.Attribute("Content") == "C").Element;
        Assert.Null(fromCanvas.Attribute("Canvas.Left")); // Canvas positions don't apply in a Grid
        Assert.Equal("onCanvas_Click", (string?)fromCanvas.Attribute("Click")); // same document: handler exists
        Assert.Contains("\n    <Button x:Name=\"button3\"", s.Doc.ToXaml()); // indented like its siblings
        UndoAllAndCheck(s);
    }

    [Fact]
    public void Paste_IntoCanvas_OffsetsCanvasPosition()
    {
        var s = Open();
        var text = s.Actions.Copy([s.Ids["onCanvas"]])!;

        var id = s.Actions.Paste(text, s.Ids["Canvas"], s.Layout, Catalog, out _)!.Single();

        var element = s.Doc.FindById(id)!.Element;
        Assert.Equal(("20", "30"), ((string?)element.Attribute("Canvas.Left"), (string?)element.Attribute("Canvas.Top")));
    }

    [Fact]
    public void Paste_FromAnotherDocument_DropsEventHandlers_AndDeclaresNamespaces()
    {
        var source = Open();
        var text = source.Actions.Copy([source.Ids["onCanvas"], source.Ids["stars"]])!;
        var target = XamlDocument.Parse("""<Window xmlns="https://github.com/avaloniaui"><StackPanel /></Window>""");
        var undo = new UndoManager(target);
        var actions = new DesignerActions(target, undo);
        var panel = target.Root.Children.Single();
        var layout = new LayoutIndex(new ElementLayout(target.Root.Id, "Window", new RectDto(0, 0, 100, 100), true, ThicknessDto.Zero, ThicknessDto.Zero, null,
            [new ElementLayout(panel.Id, "StackPanel", new RectDto(0, 0, 100, 100), true, ThicknessDto.Zero, ThicknessDto.Zero, new PanelInfo(PanelKind.StackPanel), [])]));

        actions.Paste(text, panel.Id, layout, Catalog, out _);

        var result = target.ToXaml();
        Assert.DoesNotContain("Click=", result);
        Assert.Contains("xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"", result);
        Assert.Contains("xmlns:c=\"using:My.Controls\"", result);
        Assert.Contains("<c:StarRating x:Name=\"stars\" />", result); // Grid.Row dropped in a StackPanel; name kept (free here)
        undo.Undo();
        Assert.Equal("""<Window xmlns="https://github.com/avaloniaui"><StackPanel /></Window>""", target.ToXaml());
    }

    [Fact]
    public void Paste_PlainXamlSnippet()
    {
        var s = Open();

        var id = s.Actions.Paste("<Button Content=\"From docs\" />", s.Ids["StackPanel"], s.Layout, Catalog, out _)!.Single();

        Assert.Equal(Av, s.Doc.FindById(id)!.XmlNamespace);
        Assert.Contains("<Button Content=\"From docs\" />", s.Doc.ToXaml());
    }

    [Fact]
    public void Paste_RejectsNonXamlAndRootOnlyCopies()
    {
        var s = Open();

        Assert.Null(s.Actions.Paste("hello", null, s.Layout, Catalog, out var problem));
        Assert.NotNull(problem);
        Assert.Null(s.Actions.Copy([s.Doc.Root.Id]));
    }
}
