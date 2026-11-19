using System.Xml.Linq;
using XDesigner.Core.Assets;
using XDesigner.Core.Editing;
using XDesigner.Core.Layout;
using XDesigner.Core.Metadata;
using XDesigner.Core.Project;
using XDesigner.Core.Settings;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;
using XDesigner.Protocol.Rendering;

namespace XDesigner.Core.Tests;

/// <summary>Design size, hidden controls, grid snapping, project images and settings.</summary>
public sealed class PreviewAndAssetsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("xdesigner-preview-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string UserControlSource =
        "<UserControl xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" x:Class=\"App.Card\">\n" +
        "  <StackPanel x:Name=\"panel\">\n" +
        "    <Button x:Name=\"ok\" Content=\"OK\" />\n" +
        "  </StackPanel>\n" +
        "</UserControl>\n";

    private static (XamlDocument Doc, UndoManager Undo, DesignerActions Actions) Open(string source)
    {
        var doc = XamlDocument.Parse(source);
        var undo = new UndoManager(doc);
        return (doc, undo, new DesignerActions(doc, undo));
    }

    [Fact]
    public void SetDesignSize_OnAUserControl_WritesDesignAttributesAndNamespaces_AndUndoIsExact()
    {
        var (doc, undo, actions) = Open(UserControlSource);

        Assert.True(actions.SetDesignSize(400.4, 700, isWindow: false));

        var text = doc.ToXaml();
        Assert.Contains("xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\"", text);
        Assert.Contains("xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"", text);
        Assert.Contains("mc:Ignorable=\"d\"", text);
        Assert.Contains("d:DesignWidth=\"400\"", text);
        Assert.Contains("d:DesignHeight=\"700\"", text);
        Assert.False(actions.SetDesignSize(400, 700, isWindow: false)); // already that size

        undo.Undo();
        Assert.Equal(UserControlSource, doc.ToXaml());
    }

    [Fact]
    public void SetDesignSize_KeepsExistingPrefixesAndIgnorableList()
    {
        var source = UserControlSource.Replace(
            "x:Class=\"App.Card\"",
            "xmlns:blend=\"http://schemas.microsoft.com/expression/blend/2008\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" mc:Ignorable=\"other\" x:Class=\"App.Card\"");
        var (doc, _, actions) = Open(source);

        actions.SetDesignSize(320, 200, isWindow: false);

        var text = doc.ToXaml();
        Assert.Contains("mc:Ignorable=\"other blend\"", text);
        Assert.Contains("blend:DesignWidth=\"320\"", text);
        Assert.DoesNotContain("xmlns:d=", text);
    }

    [Fact]
    public void SetDesignSize_OnAWindow_WritesWidthAndHeight()
    {
        var (doc, _, actions) = Open("<Window xmlns=\"https://github.com/avaloniaui\" Width=\"800\" Height=\"450\" />");

        actions.SetDesignSize(1280, 720, isWindow: true);

        Assert.Equal("<Window xmlns=\"https://github.com/avaloniaui\" Width=\"1280\" Height=\"720\" />", doc.ToXaml());
    }

    [Fact]
    public void DesignPreview_HiddenControls_AreHiddenInTheRenderCopyOnly()
    {
        var (doc, _, _) = Open(UserControlSource);
        var preview = new DesignPreview();
        var ok = doc.AllNodes.Single(n => n.Name == "ok").Id;

        Assert.True(preview.SetHidden(ok, true));
        Assert.True(preview.SetHidden(doc.Root.Id, true)); // remembered, but the root is never hidden

        var overrides = preview.Overrides(doc, new ControlCatalog([]));
        Assert.Equal("False", overrides[ok]["IsVisible"]);
        Assert.False(overrides.ContainsKey(doc.Root.Id));
        Assert.Contains("IsVisible=\"False\"", doc.BuildRenderCopy(overrides).ToString());
        Assert.Equal(UserControlSource, doc.ToXaml());

        Assert.True(preview.SetHidden(ok, false));
        Assert.False(preview.Overrides(doc, new ControlCatalog([])).ContainsKey(ok));
    }

    [Fact]
    public void SnapToGrid_MovesTheCornerAndResizesTheDraggedEdges()
    {
        var container = new RectDto(10, 20, 400, 300);

        Assert.Equal(new RectDto(18, 28, 50, 30), SnapEngine.SnapToGrid(new RectDto(21, 31, 50, 30), ResizeEdges.None, container, 8));
        Assert.Equal(new RectDto(21, 31, 53, 29), SnapEngine.SnapToGrid(new RectDto(21, 31, 52, 27), ResizeEdges.Right | ResizeEdges.Bottom, container, 8));
        Assert.Equal(new RectDto(21, 31, 8, 8), SnapEngine.SnapToGrid(new RectDto(21, 31, 2, 2), ResizeEdges.Right | ResizeEdges.Bottom, container, 8)); // never below one step
    }

    [Fact]
    public void ProjectAssets_ImportCopiesOnce_ReusesIdenticalFiles_AndNumbersDifferentOnes()
    {
        var project = Path.Combine(_dir, "App");
        Directory.CreateDirectory(project);
        var source = Path.Combine(_dir, "logo.png");
        File.WriteAllBytes(source, [1, 2, 3]);

        var first = ProjectAssets.Import(project, source);
        Assert.Equal(("/Assets/logo.png", true), (first.XamlPath, first.Copied));
        Assert.True(File.Exists(Path.Combine(project, "Assets", "logo.png")));

        var again = ProjectAssets.Import(project, source);
        Assert.Equal(("/Assets/logo.png", false), (again.XamlPath, again.Copied));

        var other = Path.Combine(_dir, "other", "logo.png");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllBytes(other, [9, 9]);
        Assert.Equal("/Assets/logo-2.png", ProjectAssets.Import(project, other).XamlPath);

        var inside = ProjectAssets.Import(project, Path.Combine(project, "Assets", "logo-2.png"));
        Assert.Equal(("/Assets/logo-2.png", false), (inside.XamlPath, inside.Copied));

        File.WriteAllText(Path.Combine(project, "Assets", "notes.txt"), "");
        Assert.Equal(["/Assets/logo-2.png", "/Assets/logo.png"], ProjectAssets.ListImages(project));
        Assert.Equal(Path.Combine(project, "Assets", "logo.png"), ProjectAssets.ToFilePath(project, "/Assets/logo.png"));
        Assert.Null(ProjectAssets.ToFilePath(project, "avares://Other/x.png"));
        Assert.Null(ProjectAssets.ToFilePath(project, "/../outside.png"));
        Assert.Null(ProjectAssets.ToFilePath(project, "/../App2/sibling.png")); // a sibling folder whose name starts with "App"
    }

    [Fact]
    public void EnsureAvaloniaResource_AddsAnItemGroupOnce_KeepingTheRestOfTheFile()
    {
        var path = Path.Combine(_dir, "App.csproj");
        const string original =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\r\n" +
            "  <PropertyGroup>\r\n    <OutputType>WinExe</OutputType>\r\n  </PropertyGroup>\r\n\r\n" +
            "  <ItemGroup>\r\n    <PackageReference Include=\"Avalonia\" Version=\"12.1.6\" />\r\n  </ItemGroup>\r\n\r\n" +
            "</Project>\r\n";
        File.WriteAllText(path, original);

        Assert.True(ProjectFileEditor.EnsureAvaloniaResource(path));
        var text = File.ReadAllText(path);
        Assert.Equal(
            original.Replace("</Project>", "  <ItemGroup>\r\n    <AvaloniaResource Include=\"Assets\\**\" />\r\n  </ItemGroup>\r\n\r\n</Project>"),
            text);
        Assert.True(ProjectFileEditor.CoversAssets(XDocument.Parse(text)));

        Assert.False(ProjectFileEditor.EnsureAvaloniaResource(path));
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public void EnsureAvaloniaResource_RecognisesOtherSpellings()
    {
        Assert.True(ProjectFileEditor.CoversAssets(XDocument.Parse("<Project><ItemGroup><AvaloniaResource Include=\"Styles\\**;Assets/**/*\" /></ItemGroup></Project>")));
        Assert.False(ProjectFileEditor.CoversAssets(XDocument.Parse("<Project><ItemGroup><AvaloniaResource Include=\"Styles\\**\" /></ItemGroup></Project>")));
    }

    [Fact]
    public void Settings_RoundTrip_AndRecentListStaysShortAndUnique()
    {
        var store = new SettingsStore(Path.Combine(_dir, "sub", "settings.json"));
        Assert.Equal(EditorChoice.Automatic, store.Load().Editor); // no file yet: defaults

        var settings = new DesignerSettings { Editor = EditorChoice.Custom, CustomEditorCommand = "edit \"{file}\":{line}", PreviewTheme = ThemeVariantKind.Dark, SnapToGrid = true, ToolboxWidth = 250 };
        for (var i = 0; i < 12; i++)
        {
            settings.AddRecent(Path.Combine(_dir, $"f{i}.axaml"));
        }

        settings.AddRecent(Path.Combine(_dir, "F3.AXAML"));
        Assert.Equal(DesignerSettings.MaxRecent, settings.Recent.Count);
        Assert.EndsWith("F3.AXAML", settings.Recent[0]);
        Assert.Single(settings.Recent, p => p.EndsWith("f3.axaml", StringComparison.OrdinalIgnoreCase));

        Assert.True(store.Save(settings));
        var loaded = store.Load();
        Assert.Equal((EditorChoice.Custom, "edit \"{file}\":{line}", ThemeVariantKind.Dark, true, 250d),
            (loaded.Editor, loaded.CustomEditorCommand, loaded.PreviewTheme, loaded.SnapToGrid, loaded.ToolboxWidth));
        Assert.Equal(settings.Recent, loaded.Recent);

        File.WriteAllText(store.Path, "{ not json");
        Assert.False(store.Load().SnapToGrid); // unreadable: defaults
    }
}
