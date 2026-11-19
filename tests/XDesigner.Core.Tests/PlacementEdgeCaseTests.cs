using System.Xml.Linq;
using XDesigner.Core.Editing;
using XDesigner.Core.Layout;
using XDesigner.Core.Metadata;
using XDesigner.Core.Rendering;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;
using XDesigner.Protocol.Rendering;

namespace XDesigner.Core.Tests;

/// <summary>Placement cases from the 1.0.0 code review (R15–R17, L-E1).</summary>
public class PlacementEdgeCaseTests
{
    private const string Av = ControlTypeInfo.AvaloniaXmlNamespace;

    private static ControlTypeInfo T(string name, ContentModel content) =>
        new($"Avalonia.Controls.{name}", name, Av, "Avalonia.Controls", "Avalonia.Controls", true, true, content,
            content switch { ContentModel.Children => "Children", ContentModel.SingleContent => "Content", _ => null }, null, [], null);

    private static readonly ControlCatalog Catalog = new([
        T("Window", ContentModel.SingleContent), T("Grid", ContentModel.Children), T("StackPanel", ContentModel.Children),
        T("Canvas", ContentModel.Children), T("DockPanel", ContentModel.Children), T("Button", ContentModel.SingleContent),
        T("TextBox", ContentModel.None),
    ]);

    private sealed record Form(XamlDocument Doc, UndoManager Undo, DesignerActions Actions, LayoutIndex Layout, Dictionary<string, int> Ids)
    {
        public XElement El(string name) => Doc.FindById(Ids[name])!.Element;

        public string? A(string name, string attribute) => (string?)El(name).Attribute(attribute);

        public string[] Order(string panel) =>
            El(panel).Elements().Select(e => (string?)e.Attribute(XName.Get("Name", XamlNamespaces.Xaml)) ?? "?").ToArray();

        public void UndoAll(string original)
        {
            while (Undo.CanUndo)
            {
                Undo.Undo();
            }

            Assert.Equal(original, Doc.ToXaml());
        }
    }

    /// <summary>Opens <paramref name="xaml"/> with one panel (the root's only child) laid out as <paramref name="kind"/>.</summary>
    private static Form Open(string xaml, PanelInfo panel, RectDto panelBounds, params (string Name, RectDto Bounds, ThicknessDto? Margin)[] children)
    {
        var doc = XamlDocument.Parse(xaml);
        var undo = new UndoManager(doc);
        var ids = doc.AllNodes.ToDictionary(n => n.Name ?? n.TypeName, n => n.Id);
        var panelNode = doc.Root.Children.Single();
        var layout = new LayoutIndex(new ElementLayout(doc.Root.Id, "Window", new RectDto(0, 0, 400, 300), true, ThicknessDto.Zero, ThicknessDto.Zero, null,
            [new ElementLayout(panelNode.Id, panelNode.TypeName, panelBounds, true, ThicknessDto.Zero, ThicknessDto.Zero, panel,
                children.Select(c => new ElementLayout(ids[c.Name], "Child", c.Bounds, true, c.Margin ?? ThicknessDto.Zero, ThicknessDto.Zero, null, [])).ToArray())]));
        return new Form(doc, undo, new DesignerActions(doc, undo), layout, ids);
    }

    private const string Dock = """
        <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <DockPanel>
            <TextBox x:Name="a" DockPanel.Dock="Top" />
            <TextBox x:Name="b" DockPanel.Dock="Left" />
            <TextBox x:Name="c" />
          </DockPanel>
        </Window>
        """;

    private static Form OpenDock() => Open(Dock, new PanelInfo(PanelKind.DockPanel), new RectDto(0, 0, 400, 300),
        ("a", new RectDto(0, 0, 400, 30), null), ("b", new RectDto(0, 30, 60, 270), null), ("c", new RectDto(60, 30, 340, 270), null));

    [Fact]
    public void DockPanel_MovingTheFillChildToAnEdge_DocksItAndTheNextOneFills()
    {
        var s = OpenDock();
        var plan = DropPlanner.Plan(s.Doc, Catalog, s.Layout, 70, 150, out _, s.Actions.WithDescendants([s.Ids["c"]]))!; // near the left edge

        Assert.True(s.Actions.MoveTo(s.Ids["c"], plan, s.Layout));

        Assert.Equal("Left", s.A("c", "DockPanel.Dock"));
        Assert.Equal(["a", "c", "b"], s.Order("DockPanel")); // b is now the last child, so it fills
        s.UndoAll(Dock);
    }

    [Fact]
    public void DockPanel_MovingADockedChildToTheCentre_MakesItTheFillChild()
    {
        var s = OpenDock();
        var plan = DropPlanner.Plan(s.Doc, Catalog, s.Layout, 200, 150, out _, s.Actions.WithDescendants([s.Ids["a"]]))!;

        Assert.True(s.Actions.MoveTo(s.Ids["a"], plan, s.Layout));

        Assert.Null(s.A("a", "DockPanel.Dock"));
        Assert.Equal(["b", "c", "a"], s.Order("DockPanel"));
        s.UndoAll(Dock);
    }

    [Fact]
    public void Grid_ResizingOneEdgeOfAStretchedControl_KeepsTheOtherDimension()
    {
        const string xaml = """
            <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Grid>
                <TextBox x:Name="box" />
              </Grid>
            </Window>
            """;
        var s = Open(xaml, new PanelInfo(PanelKind.Grid, RowOffsets: [0, 230], ColumnOffsets: [0, 300]), new RectDto(0, 0, 300, 230),
            ("box", new RectDto(0, 0, 300, 230), null));

        s.Actions.Resize(s.Ids["box"], new RectDto(0, 0, 150, 230), ResizeEdges.Right, s.Layout, Catalog);

        Assert.Equal(("150", "230"), (s.A("box", "Width"), s.A("box", "Height")));
        Assert.Single(s.El("box").Attributes("Width"));
        s.UndoAll(xaml);
    }

    [Fact]
    public void Canvas_NudgingAControlWithAMargin_MovesByOnePixel()
    {
        const string xaml = """
            <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Canvas>
                <Button x:Name="ok" Canvas.Left="20" Canvas.Top="10" Margin="4" />
              </Canvas>
            </Window>
            """;
        // Bounds exclude the margin: the button is drawn at Canvas.Left + Margin.Left.
        var s = Open(xaml, new PanelInfo(PanelKind.Canvas), new RectDto(0, 0, 400, 300), ("ok", new RectDto(24, 14, 50, 25), new ThicknessDto(4, 4, 4, 4)));

        s.Actions.Nudge([s.Ids["ok"]], 1, 0, s.Layout, Catalog);

        Assert.Equal(("21", "10"), (s.A("ok", "Canvas.Left"), s.A("ok", "Canvas.Top")));
        s.UndoAll(xaml);
    }

    private const string Stack = """
        <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <StackPanel>
            <Button x:Name="a" />
            <Button x:Name="b" />
            <Button x:Name="c" />
          </StackPanel>
        </Window>
        """;

    private static Form OpenStack() => Open(Stack, new PanelInfo(PanelKind.StackPanel, LayoutOrientation.Vertical), new RectDto(0, 0, 100, 75),
        ("a", new RectDto(0, 0, 100, 25), null), ("b", new RectDto(0, 25, 100, 25), null), ("c", new RectDto(0, 50, 100, 25), null));

    [Theory]
    [InlineData(new[] { "a", "b" }, 1, new[] { "c", "a", "b" })]
    [InlineData(new[] { "b", "a" }, 1, new[] { "c", "a", "b" })] // selection order doesn't matter
    [InlineData(new[] { "b", "c" }, -1, new[] { "b", "c", "a" })]
    [InlineData(new[] { "b", "c" }, 1, new[] { "a", "b", "c" })] // the block is already last
    [InlineData(new[] { "a", "c" }, 1, new[] { "b", "a", "c" })]
    public void Stack_NudgingSeveralSelectedControls_MovesThemAsABlock(string[] selected, int dy, string[] expected)
    {
        var s = OpenStack();

        s.Actions.Nudge(selected.Select(n => s.Ids[n]).ToList(), 0, dy, s.Layout, Catalog);

        Assert.Equal(expected, s.Order("StackPanel"));
        s.UndoAll(Stack);
    }
}
