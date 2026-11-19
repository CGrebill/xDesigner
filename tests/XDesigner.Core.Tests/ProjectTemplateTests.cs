using XDesigner.Core.Project;
using XDesigner.Core.Xaml;

namespace XDesigner.Core.Tests;

public sealed class ProjectTemplateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("xdesigner-template-").FullName;

    public void Dispose()
    {
        // A build server or a virus scanner may still hold a file briefly: try a few times before leaving it to Windows.
        for (var attempt = 0; attempt < 10 && Directory.Exists(_dir); attempt++)
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }

    [Theory]
    [InlineData("HelloAvalonia", null)]
    [InlineData("My-App.Tools", null)]
    [InlineData("", "Enter")]
    [InlineData("1App", "start")]
    [InlineData("My App", "no spaces")]
    [InlineData("App.", "no spaces")]
    [InlineData("Window", "type name")]
    [InlineData("Shop.Application", "type name")]
    [InlineData("Avalonia", "package")]
    [InlineData("avalonia.Desktop", "package")]
    [InlineData("AvaloniaDemo", null)]
    [InlineData("Shop.App", null)]
    public void ProjectNames_AreValidated(string name, string? problemPart)
    {
        var problem = ProjectTemplates.ValidateProjectName(name);
        if (problemPart is null)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.Contains(problemPart, problem);
        }
    }

    [Theory]
    [InlineData("HelloAvalonia", "HelloAvalonia")]
    [InlineData("My-App.Tools", "My_App.Tools")]
    [InlineData("Shop.2026", "Shop._2026")]
    public void Namespace_IsMadeFromTheName(string name, string expected) => Assert.Equal(expected, ProjectTemplates.NamespaceFor(name));

    [Fact]
    public void CreateApp_WritesEveryFileWithTokensReplaced()
    {
        var project = ProjectTemplates.CreateApp(_dir, "My-App", "My <First> App");
        var dir = Path.GetDirectoryName(project)!;

        Assert.Equal(Path.Combine(dir, "My-App.csproj"), project);
        string[] expected = ["My-App.csproj", "Program.cs", "App.axaml", "App.axaml.cs", "MainWindow.axaml", "MainWindow.axaml.cs", "app.manifest", "global.json", ".gitignore"];
        Assert.All(expected, f => Assert.True(File.Exists(Path.Combine(dir, f)), f));
        Assert.True(Directory.Exists(Path.Combine(dir, "Assets")));

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            Assert.DoesNotContain("$", File.ReadAllText(file).Replace("$\"", "", StringComparison.Ordinal)); // no tokens left
        }

        var window = XamlDocument.Load(Path.Combine(dir, "MainWindow.axaml"));
        Assert.Equal("My_App.MainWindow", (string?)window.Root.Element.Attribute(System.Xml.Linq.XName.Get("Class", XamlNamespaces.Xaml)));
        Assert.Equal("My <First> App", (string?)window.Root.Element.Attribute("Title"));
        Assert.Contains($"Version=\"{ProjectTemplates.AvaloniaVersion}\"", File.ReadAllText(project));
        Assert.Contains("namespace My_App;", File.ReadAllText(Path.Combine(dir, "App.axaml.cs")));

        Assert.Throws<ProjectException>(() => ProjectTemplates.CreateApp(_dir, "My-App", "again")); // folder not empty
    }

    [Fact]
    public void CreateApp_TitleStartingWithABrace_IsNotAMarkupExtension()
    {
        var project = ProjectTemplates.CreateApp(_dir, "Braces", "{My App}");

        var window = XamlDocument.Load(Path.Combine(Path.GetDirectoryName(project)!, "MainWindow.axaml"));
        Assert.Equal("{}{My App}", (string?)window.Root.Element.Attribute("Title"));
    }

    [Fact]
    public void CreateItem_UsesTheFolderNamespace_AndRefusesDuplicates()
    {
        var info = new ProjectInfo(Path.Combine(_dir, "App.csproj"), "App", "App", "Shop.App", "", "net10.0", "WinExe");
        var views = Path.Combine(_dir, "Views", "Dialogs");

        var xaml = ProjectTemplates.CreateItem(info, views, "AboutBox", ItemKind.UserControl);

        Assert.Equal(Path.Combine(views, "AboutBox.axaml"), xaml);
        Assert.Contains("x:Class=\"Shop.App.Views.Dialogs.AboutBox\"", File.ReadAllText(xaml));
        var code = File.ReadAllText(xaml + ".cs");
        Assert.Contains("namespace Shop.App.Views.Dialogs;", code);
        Assert.Contains("public partial class AboutBox : UserControl", code);

        Assert.NotNull(ProjectTemplates.ValidateItemName(views, "AboutBox"));
        Assert.NotNull(ProjectTemplates.ValidateItemName(views, "class"));
        Assert.Contains("derive from itself", ProjectTemplates.ValidateItemName(views, "Window"));
        Assert.Contains("derive from itself", ProjectTemplates.ValidateItemName(views, "UserControl"));
        Assert.Contains("x:Class=\"Shop.App.Settings\"", File.ReadAllText(ProjectTemplates.CreateItem(info, _dir, "Settings", ItemKind.Window)));
    }

    /// <summary>A new project builds as created. Needs the SDK and the Avalonia packages (cached or online).</summary>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task CreatedApp_Builds()
    {
        var project = ProjectTemplates.CreateApp(_dir, "HelloAvalonia", "Hello Avalonia");
        var info = await ProjectReader.ReadAsync(project, TestContext.Current.CancellationToken);
        ProjectTemplates.CreateItem(info, Path.Combine(info.Directory, "Views"), "SidePanel", ItemKind.UserControl);

        var result = await BuildRunner.BuildAsync(project, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
        Assert.Empty(result.Warnings);
        Assert.True(File.Exists(info.TargetPath));
        Assert.Equal(12, ProjectReader.AvaloniaVersionOf(info.TargetPath)?.Major);
    }
}
