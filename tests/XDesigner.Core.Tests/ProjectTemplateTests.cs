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
    [Theory]
    [InlineData(Core.Frameworks.FrameworkId.Avalonia12)]
    [InlineData(Core.Frameworks.FrameworkId.Avalonia11)]
    [Trait("Category", "Slow")]
    public async Task CreatedApp_Builds(Core.Frameworks.FrameworkId framework)
    {
        var profile = Core.Frameworks.FrameworkProfile.For(framework);
        var project = ProjectTemplates.CreateApp(_dir, "HelloAvalonia", "Hello Avalonia", profile);
        var info = await ProjectReader.ReadAsync(project, TestContext.Current.CancellationToken);
        ProjectTemplates.CreateItem(info, Path.Combine(info.Directory, "Views"), "SidePanel", ItemKind.UserControl);

        var result = await BuildRunner.BuildAsync(project, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
        Assert.Empty(result.Warnings);
        Assert.True(File.Exists(info.TargetPath));
        Assert.Equal(profile, info.DetectFramework().Profile);
        Assert.Equal(profile, Core.Frameworks.FrameworkDetector.FromProjectFile(project).Profile); // before a build too
    }


    /// <summary>A new WinUI project, unpackaged or packaged, builds as created and reads back as such. Needs the SDK and the Windows App SDK package.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Slow")]
    public async Task CreatedWinUIApp_Builds(bool packaged)
    {
        var profile = Core.Frameworks.FrameworkProfile.WinUI3;
        var project = ProjectTemplates.CreateApp(_dir, "HelloWinUI", "Hello {WinUI} & co", profile, packaged);
        var info = await ProjectReader.ReadAsync(project, TestContext.Current.CancellationToken);
        var page = ProjectTemplates.CreateItem(info, Path.Combine(info.Directory, "Pages"), "SettingsPage", ItemKind.Page, profile);
        ProjectTemplates.CreateItem(info, Path.Combine(info.Directory, "Controls"), "Card", ItemKind.UserControl, profile);

        var result = await BuildRunner.BuildAsync(project, platform: info.Platform, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal((ProjectReader.NativePlatform, packaged), (info.Platform, info.IsPackaged));
        Assert.True(File.Exists(info.TargetPath), info.TargetPath);
        Assert.Equal(packaged, File.Exists(Path.Combine(Path.GetDirectoryName(info.TargetPath)!, "AppxManifest.xml")));
        Assert.EndsWith(Path.Combine("Pages", "SettingsPage.xaml"), page);
        Assert.Contains("x:Class=\"HelloWinUI.Pages.SettingsPage\"", File.ReadAllText(page));
        Assert.Equal(profile, info.DetectFramework().Profile);
        Assert.Contains("Title=\"Hello {WinUI} &amp; co\"", File.ReadAllText(Path.Combine(info.Directory, "MainWindow.xaml")));
    }


    /// <summary>A new MAUI project, unpackaged or packaged, builds for Windows as created. Needs the SDK and the MAUI workload.</summary>
    [Theory]
    [InlineData(Core.Frameworks.FrameworkId.Maui11, false)]
    [InlineData(Core.Frameworks.FrameworkId.Maui11, true)]
    [InlineData(Core.Frameworks.FrameworkId.Maui10, false)]
    [Trait("Category", "Slow")]
    public async Task CreatedMauiApp_Builds(Core.Frameworks.FrameworkId framework, bool packaged)
    {
        var profile = Core.Frameworks.FrameworkProfile.For(framework);
        var project = ProjectTemplates.CreateApp(_dir, "HelloMaui", "Hello MAUI", profile, packaged);
        var info = await ProjectReader.ReadAsync(project, TestContext.Current.CancellationToken);
        var page = ProjectTemplates.CreateItem(info, Path.Combine(info.Directory, "Pages"), "SettingsPage", ItemKind.Page, profile);
        ProjectTemplates.CreateItem(info, Path.Combine(info.Directory, "Views"), "Card", ItemKind.UserControl, profile);

        var result = await BuildRunner.BuildAsync(project, platform: info.Platform, framework: info.BuildFramework, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal($"net{profile.MajorVersion}.0-windows10.0.19041.0", info.BuildFramework);
        Assert.Equal(packaged, info.IsPackaged);
        Assert.True(File.Exists(info.TargetPath), info.TargetPath);
        Assert.Contains("<ContentPage", File.ReadAllText(page), StringComparison.Ordinal);
        Assert.Equal(profile, info.DetectFramework().Profile);
        Assert.True(Directory.Exists(Path.Combine(info.Directory, "Resources", "Images")));
    }
    [Fact]
    public void WinUIItems_AreXamlFiles_AndPagesAreWinUIOnly()
    {
        var info = new ProjectInfo(Path.Combine(_dir, "App.csproj"), "App", "App", "Shop.App", "", "net10.0-windows10.0.19041.0", "WinExe");
        var winui = Core.Frameworks.FrameworkProfile.WinUI3;

        var window = ProjectTemplates.CreateItem(info, _dir, "AboutWindow", ItemKind.Window, winui);

        Assert.EndsWith("AboutWindow.xaml", window);
        Assert.True(File.Exists(window + ".cs"));
        Assert.Contains("already exists", ProjectTemplates.ValidateItemName(_dir, "AboutWindow", winui));
        Assert.Throws<ProjectException>(() => ProjectTemplates.CreateItem(info, _dir, "Start", ItemKind.Page));
        Assert.Equal("My-App.Tools", ProjectTemplates.PackageName("My_App.Tools"));
        Assert.Equal("ABApp", ProjectTemplates.PackageName("AB"));
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], ProjectTemplates.SolidPng(4, 1, 2, 3)[..4]);
    }
    [Fact]
    public void TemplateVersions_MatchTheRenderHostsPackages()
    {
        var props = System.Xml.Linq.XDocument.Load(Path.Combine(RepoRoot(), "Directory.Packages.props"));
        string Property(string name) => props.Descendants(name).Single().Value;

        Assert.Equal(Property("AvaloniaVersion"), Core.Frameworks.FrameworkProfile.Avalonia12.TemplateVersion);
        Assert.Equal(Property("Avalonia11Version"), Core.Frameworks.FrameworkProfile.Avalonia11.TemplateVersion);
        Assert.Equal(Property("WindowsAppSdkVersion"), Core.Frameworks.FrameworkProfile.WinUI3.TemplateVersion);
        Assert.Equal(Property("Maui10Version"), Core.Frameworks.FrameworkProfile.Maui10.TemplateVersion);
        Assert.Equal(Property("Maui11Version"), Core.Frameworks.FrameworkProfile.Maui11.TemplateVersion);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "xDesigner.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No xDesigner.slnx above the test output.");
    }
}
