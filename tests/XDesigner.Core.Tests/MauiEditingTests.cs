using XDesigner.Core.Editing;
using XDesigner.Core.Frameworks;
using XDesigner.Core.Layout;
using XDesigner.Core.Metadata;
using XDesigner.Core.Rendering;
using XDesigner.Core.Toolbox;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;
using XDesigner.Protocol.Rendering;

namespace XDesigner.Core.Tests;

/// <summary>
/// Editing .NET MAUI XAML: MAUI 11's files without namespace declarations (§5.3) and MAUI's layout vocabulary (§7.1:
/// WidthRequest, HorizontalOptions, AbsoluteLayout.LayoutBounds).
/// </summary>
public class MauiEditingTests
{
    private const string Controls = "Microsoft.Maui.Controls";

    private static ControlTypeInfo T(string name, ContentModel content, params string[] bases) =>
        new($"{Controls}.{name}", name, FrameworkProfile.MauiNamespace, Controls, "Microsoft.Maui.Controls", true, true, content,
            content is ContentModel.Children ? "Children" : content is ContentModel.SingleContent ? "Content" : null, null, bases);

    private static readonly ControlCatalog Catalog = new(
    [
        T("ContentPage", ContentModel.SingleContent, $"{Controls}.Page"), T("Grid", ContentModel.Children, $"{Controls}.Layout"),
        T("AbsoluteLayout", ContentModel.Children, $"{Controls}.Layout"), T("BoxView", ContentModel.None), T("Label", ContentModel.None),
    ], FrameworkProfile.Maui11);

    /// <summary>As MAUI 11's template writes pages: no xmlns, an undeclared x: prefix.</summary>
    private static readonly string Page = """
        <ContentPage x:Class="App.MainPage">
          <Grid RowDefinitions="50,*">
            <AbsoluteLayout Grid.Row="1">
              <BoxView x:Name="box" AbsoluteLayout.LayoutBounds="10,20,40,40" />
            </AbsoluteLayout>
          </Grid>
        </ContentPage>

        """.ReplaceLineEndings("\n");

    private static (XamlDocument Doc, UndoManager Undo, DesignerActions Actions, LayoutIndex Layout, Dictionary<string, int> Ids) Setup()
    {
        var doc = XamlDocument.Parse(Page);
        var undo = new UndoManager(doc);
        var ids = doc.AllNodes.ToDictionary(n => n.Name ?? n.TypeName, n => n.Id);
        ElementLayout L(string key, double x, double y, double w, double h, PanelInfo? panel = null, params ElementLayout[] children) =>
            new(ids[key], $"{Controls}.{key}", new RectDto(x, y, w, h), true, ThicknessDto.Zero, ThicknessDto.Zero, panel, children);
        var layout = new LayoutIndex(
            L("ContentPage", 0, 0, 400, 300, null,
                L("Grid", 0, 0, 400, 300, new PanelInfo(PanelKind.Grid, RowOffsets: [0, 50, 300], ColumnOffsets: [0, 400]),
                    L("AbsoluteLayout", 0, 50, 400, 250, new PanelInfo(PanelKind.Canvas),
                        L("box", 10, 70, 40, 40)))));
        return (doc, undo, new DesignerActions(doc, undo), layout, ids);
    }

    [Fact]
    public void FileWithoutNamespaces_IsMauiAndRoundTripsByteForByte()
    {
        var doc = XamlDocument.Parse(Page);

        Assert.Equal(FrameworkFamily.Maui, doc.Profile.Family);
        Assert.Equal(FrameworkProfile.MauiNamespace, doc.Xml.Root!.Name.NamespaceName);
        Assert.Equal("box", doc.AllNodes.Single(n => n.TypeName == "BoxView").Name);
        Assert.Equal(Page, doc.ToXaml());
    }

    [Fact]
    public void AddingControls_UsesTheImplicitPrefixes_AndDeclaresNothing()
    {
        var (doc, _, actions, layout, ids) = Setup();
        var plan = DropPlanner.Plan(doc, Catalog, layout, 100.4, 150, out var reason) ?? throw new Xunit.Sdk.XunitException(reason);

        actions.AddControl(ToolboxCatalog.ItemFor(Catalog.Find($"{Controls}.Label")!), plan);

        var text = doc.ToXaml();
        Assert.Equal(ids["AbsoluteLayout"], plan.ContainerId);
        Assert.Contains("<Label x:Name=\"label1\" AbsoluteLayout.LayoutBounds=\"100,100,-1,-1\" />", text, StringComparison.Ordinal);
        Assert.DoesNotContain("xmlns", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PositioningInAGridCell_WritesStartOptionsAndASize()
    {
        var (doc, _, actions, layout, ids) = Setup();
        var plan = DropPlanner.PlanInto(doc, Catalog, layout, ids["Grid"], 30, 10, new HashSet<int>())!;

        actions.AddControl(ToolboxCatalog.ItemFor(Catalog.Find($"{Controls}.Label")!) with
        {
            Attributes = new Dictionary<string, string> { ["WidthRequest"] = "150" },
        }, plan);

        Assert.Contains("<Label x:Name=\"label1\" WidthRequest=\"150\" HorizontalOptions=\"Start\" VerticalOptions=\"Start\" Margin=\"30,10,0,0\" />",
            doc.ToXaml(), StringComparison.Ordinal);
    }

    [Fact]
    public void MovingInAnAbsoluteLayout_ChangesTheBoundsPosition_AndKeepsItsSize()
    {
        var (doc, undo, actions, layout, ids) = Setup();

        Assert.Equal(1, actions.MoveBy([ids["box"]], 5, 6, layout, Catalog));

        Assert.Contains("AbsoluteLayout.LayoutBounds=\"15,26,40,40\"", doc.ToXaml(), StringComparison.Ordinal);
        undo.Undo();
        Assert.Equal(Page, doc.ToXaml());
    }

    [Fact]
    public void ResizingInAnAbsoluteLayout_ChangesTheBoundsSize()
    {
        var (doc, _, actions, layout, ids) = Setup();

        Assert.True(actions.Resize(ids["box"], new RectDto(10, 70, 60, 40), ResizeEdges.Right, layout, Catalog));

        var text = doc.ToXaml();
        Assert.Contains("AbsoluteLayout.LayoutBounds=\"10,20,60,40\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("WidthRequest", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderCopy_DeclaresTheImplicitNamespaces()
    {
        var copy = RenderCopyBuilder.Build(Page);

        Assert.Contains($"xmlns=\"{FrameworkProfile.MauiNamespace}\"", copy, StringComparison.Ordinal);
        Assert.Contains($"xmlns:x=\"{FrameworkProfile.Xaml2009Namespace}\"", copy, StringComparison.Ordinal);
        Assert.Contains("x:Class=\"App.MainPage\"", copy, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclaredMauiNamespaces_StillWork()
    {
        var source = $"<ContentPage xmlns=\"{FrameworkProfile.MauiNamespace}\" xmlns:x=\"{FrameworkProfile.Xaml2009Namespace}\"><Label x:Name=\"a\" /></ContentPage>";

        var doc = XamlDocument.Parse(source);

        Assert.Equal(FrameworkFamily.Maui, doc.Profile.Family);
        Assert.Null(doc.Xml.Annotation<ImplicitNamespaces>());
        Assert.Equal(source, doc.ToXaml());
    }

    [Theory]
    [InlineData("HorizontalAlignment", "Left", "HorizontalOptions", "Start")]
    [InlineData("VerticalAlignment", "Stretch", "VerticalOptions", "Fill")]
    [InlineData("Width", "120", "WidthRequest", "120")]
    public void Vocabulary_TranslatesCanonicalLayoutNames(string canonical, string value, string maui, string mauiValue)
    {
        var element = new System.Xml.Linq.XElement("Label");

        LayoutVocabulary.Maui.Apply(element, [new(canonical, value)]);

        Assert.Equal(mauiValue, (string?)element.Attribute(maui));
        Assert.Equal(value, LayoutVocabulary.Maui.Get(element, canonical));
        Assert.Equal(value, LayoutVocabulary.Xaml.Apply(new System.Xml.Linq.XElement("A"), canonical, value));
    }
}

internal static class LayoutVocabularyTestExtensions
{
    /// <summary>Applies one canonical attribute and reads it back as written (the identity for <see cref="LayoutVocabulary.Xaml"/>).</summary>
    public static string? Apply(this LayoutVocabulary vocabulary, System.Xml.Linq.XElement element, string canonical, string value)
    {
        vocabulary.Apply(element, [new(canonical, value)]);
        return (string?)element.Attribute(canonical);
    }
}
