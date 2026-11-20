using System.Xml.Linq;
using XDesigner.Core.Assets;
using XDesigner.Core.Editing;
using XDesigner.Core.Frameworks;
using XDesigner.Core.Metadata;
using XDesigner.Core.Project;
using XDesigner.Core.Toolbox;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;

namespace XDesigner.Core.Tests;

/// <summary>Editing WinUI 3 XAML: what differs from Avalonia goes through <see cref="FrameworkProfile.WinUI3"/>.</summary>
public sealed class WinUIProfileTests : IDisposable
{
    private const string Ns = """xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" """;
    private const string Controls = "Microsoft.UI.Xaml.Controls";

    private static readonly ControlCatalog Catalog = new(
    [
        T("Grid", ContentModel.Children, $"{Controls}.Panel"), T("StackPanel", ContentModel.Children, $"{Controls}.Panel"),
        T("Button", ContentModel.SingleContent), T("TextBlock", ContentModel.Inlines),
        T("TabView", ContentModel.Items), T("TabViewItem", ContentModel.SingleContent),
        T("ListView", ContentModel.Items, $"{Controls}.ListViewBase"), T("ComboBox", ContentModel.Items, $"{Controls}.Primitives.Selector"),
        T("MenuBar", ContentModel.Items), T("Page", ContentModel.SingleChild, $"{Controls}.UserControl"),
        new("Microsoft.UI.Xaml.Window", "Window", FrameworkProfile.WinUINamespace, "Microsoft.UI.Xaml", "Microsoft.WinUI", true, true, ContentModel.SingleContent, "Content", "Activated", []),
    ], FrameworkProfile.WinUI3);

    private readonly string _dir = Directory.CreateTempSubdirectory("xdesigner-winui-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ControlTypeInfo T(string name, ContentModel content, params string[] bases) =>
        new($"{Controls}.{name}", name, FrameworkProfile.WinUINamespace, Controls, "Microsoft.WinUI", true, true, content, null, null, bases);

    private static (XamlDocument Doc, UndoManager Undo, DesignerActions Actions) Open(string source)
    {
        var doc = XamlDocument.Parse(source);
        var undo = new UndoManager(doc);
        return (doc, undo, new DesignerActions(doc, undo));
    }

    [Fact]
    public void WinUIDocuments_GetTheWinUIProfile()
    {
        var (doc, _, _) = Open($"<Page {Ns}><Grid /></Page>");

        Assert.Same(FrameworkProfile.WinUI3, doc.Profile);
        Assert.True(doc.Profile.IsSupported);
    }

    [Fact]
    public void GridDefinitions_AreWrittenAsPropertyElements()
    {
        var (doc, undo, actions) = Open($"<Page {Ns}>\n  <Grid x:Name=\"grid\">\n    <Button />\n  </Grid>\n</Page>\n");
        var grid = doc.AllNodes.Single(n => n.TypeName == "Grid");

        Assert.True(actions.SetGridDefinitions(grid.Id, [new(GridUnit.Auto), new(GridUnit.Star)], []));

        var element = doc.FindById(grid.Id)!.Element;
        Assert.Null(element.Attribute("RowDefinitions"));
        var rows = GridDefinitions.Read(element, rows: true)!;
        Assert.Equal(["Auto", "*"], rows.Select(r => r.LengthText));
        Assert.Contains("<Grid.RowDefinitions>", doc.ToXaml(), StringComparison.Ordinal);

        undo.Undo();
        Assert.Empty(GridDefinitions.Read(doc.FindById(grid.Id)!.Element, rows: true)!);
    }

    [Fact]
    public void TabView_IsEditedThroughTabItems_AndNewTabsGetAContentGrid()
    {
        var (doc, _, actions) = Open($"<Page {Ns}><TabView x:Name=\"tabs\" /></Page>");
        var tabs = doc.AllNodes.Single(n => n.TypeName == "TabView");
        var kind = ItemsEditing.KindOf(Catalog, Catalog.Resolve(tabs))!;

        Assert.Equal(("TabViewItem", "Header", "TabItems"), (kind.ItemType, kind.TextProperty, kind.ItemsProperty));
        actions.SetItems(tabs.Id, [new ItemDraft(null, "General"), new ItemDraft(null, "Advanced")], kind);

        var items = doc.FindById(tabs.Id)!.Element.Elements().ToList();
        Assert.Equal(["General", "Advanced"], items.Select(i => (string?)i.Attribute("Header")));
        Assert.All(items, i => Assert.Equal("Grid", i.Elements().Single().Name.LocalName));
        Assert.Equal(2, ItemsEditing.Read(doc.FindById(tabs.Id)!, kind).Count);
    }

    [Fact]
    public void ItemsKinds_FollowTheWinUITable()
    {
        Assert.Equal("ListViewItem", ItemsEditing.KindOf(Catalog, Catalog.Find($"{Controls}.ListView"))!.ItemType);
        Assert.Equal("ComboBoxItem", ItemsEditing.KindOf(Catalog, Catalog.Find($"{Controls}.ComboBox"))!.ItemType);
        Assert.Null(ItemsEditing.KindOf(Catalog, Catalog.Find($"{Controls}.MenuBar"))); // edited in XAML
    }

    [Fact]
    public void DesignPreview_OpensTheSelectedTabOfATabView()
    {
        var (doc, _, _) = Open($"<Page {Ns}><TabView><TabViewItem Header=\"A\" /><TabViewItem Header=\"B\"><Button /></TabViewItem></TabView></Page>");
        var preview = new DesignPreview();
        var tabView = doc.AllNodes.Single(n => n.TypeName == "TabView");
        var button = doc.AllNodes.Single(n => n.TypeName == "Button");

        Assert.Equal("0", preview.Overrides(doc, Catalog)[tabView.Id]["SelectedIndex"]);
        Assert.True(preview.Follow(doc, Catalog, [button.Id]));
        Assert.Equal("1", preview.Overrides(doc, Catalog)[tabView.Id]["SelectedIndex"]);
    }

    [Fact]
    public void HiddenControls_AreCollapsed()
    {
        var (doc, _, _) = Open($"<Page {Ns}><StackPanel><Button /></StackPanel></Page>");
        var preview = new DesignPreview();
        var button = doc.AllNodes.Single(n => n.TypeName == "Button");

        preview.SetHidden(button.Id, true);

        Assert.Equal("Collapsed", preview.Overrides(doc, Catalog)[button.Id]["Visibility"]);
    }

    [Fact]
    public void KeepingAWindowsSize_WritesDesignSize_AsAWinUIWindowHasNoWidth()
    {
        var (doc, _, actions) = Open($"<Window {Ns}><Grid /></Window>");

        Assert.True(actions.SetDesignSize(800, 600, isWindow: true));

        var root = doc.Root.Element;
        Assert.Null(root.Attribute("Width"));
        Assert.Equal("800", (string?)root.Attribute(XName.Get("DesignWidth", FrameworkProfile.BlendDesignNamespace)));
    }

    [Fact]
    public void Images_AreAddressedWithMsAppx_AndShippedAsContent()
    {
        var source = Path.Combine(_dir, "logo.png");
        File.WriteAllBytes(source, [1, 2, 3]);
        var project = Path.Combine(_dir, "App");
        Directory.CreateDirectory(project);

        var import = ProjectAssets.Import(project, source, FrameworkProfile.WinUI3);

        Assert.Equal("ms-appx:///Assets/logo.png", import.XamlPath);
        Assert.Equal(["ms-appx:///Assets/logo.png"], ProjectAssets.ListImages(project, FrameworkProfile.WinUI3));
        Assert.Equal(Path.Combine(project, "Assets", "logo.png"), ProjectAssets.ToFilePath(project, import.XamlPath));

        var csproj = Path.Combine(project, "App.csproj");
        File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <Content Include=\"Assets\\SplashScreen.png\" />\n  </ItemGroup>\n</Project>\n");
        Assert.True(ProjectFileEditor.EnsureAssetsItem(csproj, FrameworkProfile.WinUI3));
        var text = File.ReadAllText(csproj);
        Assert.Contains("<Content Include=\"Assets\\**\" Exclude=\"@(Content)\" CopyToOutputDirectory=\"PreserveNewest\" />", text, StringComparison.Ordinal);
        Assert.False(ProjectFileEditor.EnsureAssetsItem(csproj, FrameworkProfile.WinUI3));
    }

    [Fact]
    public void Toolbox_UsesTheWinUIList_AndLeavesWindowsAndPagesOut()
    {
        var categories = ToolboxCatalog.Build(Catalog, "App");

        var common = categories.First(c => c.Name == "Common").Items.Select(i => i.DisplayName).ToList();
        Assert.Contains("Button", common);
        Assert.Contains(categories.SelectMany(c => c.Items), i => i.DisplayName == "TabView");
        Assert.DoesNotContain(categories.SelectMany(c => c.Items), i => i.DisplayName is "Window" or "Page");
    }
}
