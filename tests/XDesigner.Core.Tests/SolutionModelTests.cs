using XDesigner.Core.Frameworks;
using XDesigner.Core.Project;

namespace XDesigner.Core.Tests;

public sealed class SolutionModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("xdesigner-solution-").FullName;

    public void Dispose()
    {
        for (var attempt = 0; attempt < 10 && Directory.Exists(_dir); attempt++)
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200); // a build server may still hold a file
            }
        }
    }

    private SolutionProject Project(string relativeDir, string outputType, FrameworkProfile? profile, int xamlFiles = 1)
    {
        var dir = Path.Combine(_dir, relativeDir);
        var name = Path.GetFileName(relativeDir);
        var info = new ProjectInfo(Path.Combine(dir, name + ".csproj"), name, name, name, Path.Combine(dir, "bin", name + ".dll"), "net10.0", outputType);
        var framework = profile is null ? DetectedFramework.Unknown : new DetectedFramework(profile.Family, new Version(profile.MajorVersion, 0), profile);
        return new SolutionProject(info, framework, Enumerable.Range(0, xamlFiles).Select(i => Path.Combine(dir, $"View{i}.axaml")).ToList());
    }

    [Fact]
    public void FilesBelongToTheInnermostProject_AndTheStartupIsTheFirstDesignableApp()
    {
        var library = Project("Controls", "Library", FrameworkProfile.Avalonia12);
        var tool = Project("Tools", "Exe", null, xamlFiles: 0);
        var app = Project("App", "WinExe", FrameworkProfile.Avalonia11);
        var nested = Project(Path.Combine("App", "Plugins", "Plugin"), "Library", FrameworkProfile.Avalonia11);
        var solution = SolutionModel.Of(Path.Combine(_dir, "All.slnx"), [library, tool, app, nested]);

        Assert.Equal(app, solution.DefaultStartup);
        Assert.Equal(nested, solution.ProjectContaining(Path.Combine(_dir, "App", "Plugins", "Plugin", "Views", "A.axaml")));
        Assert.Equal(app, solution.ProjectContaining(Path.Combine(_dir, "App", "MainWindow.axaml")));
        Assert.Null(solution.ProjectContaining(Path.Combine(_dir, "Elsewhere", "X.axaml")));
        Assert.Equal([library, app, nested], solution.DesignableProjects);
        Assert.Equal(Path.Combine(_dir, "All.slnx"), solution.BuildTarget);
        Assert.Equal("App (Avalonia 11)", app.Label);
    }

    /// <summary>A solution built project by project (a WinUI project needs its platform) still builds each as part of it (review L16).</summary>
    [Fact]
    public void ProjectByProjectSteps_CarryTheSolution()
    {
        var app = Project("App", "WinExe", FrameworkProfile.WinUI3);
        app = app with { Info = app.Info with { Platform = "x64" } };
        var library = Project("Controls", "Library", FrameworkProfile.Avalonia12);
        var path = Path.Combine(_dir, "All.slnx");

        var steps = SolutionModel.Of(path, [app, library]).BuildSteps;

        Assert.Equal(2, steps.Count);
        Assert.All(steps, s => Assert.Equal(path, s.Solution));
        Assert.Equal("x64", steps[0].Platform);
    }

    [Fact]
    public void ASingleProject_IsBuiltOnItsOwn()
    {
        var app = Project("App", "WinExe", FrameworkProfile.Avalonia12);
        var solution = SolutionModel.Of(null, [app]);

        Assert.Equal(app.Info.ProjectPath, solution.BuildTarget);
        Assert.Equal("App", solution.Name);
    }

    /// <summary>A solution mixing Avalonia 11 and 12: both are read, each with its own framework. Needs the SDK.</summary>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task MixedSolution_ReadsEveryProjectWithItsFramework()
    {
        var twelve = ProjectTemplates.CreateApp(_dir, "Twelve", "Twelve", FrameworkProfile.Avalonia12);
        var eleven = ProjectTemplates.CreateApp(_dir, "Eleven", "Eleven", FrameworkProfile.Avalonia11);
        var slnx = Path.Combine(_dir, "Mixed.slnx");
        File.WriteAllText(slnx, "<Solution>\n  <Project Path=\"Twelve/Twelve.csproj\" />\n  <Project Path=\"Eleven/Eleven.csproj\" />\n</Solution>\n");

        var problems = new List<string>();
        var solution = await SolutionModel.OpenAsync(slnx, problems.Add, TestContext.Current.CancellationToken);

        Assert.Empty(problems);
        Assert.Equal(FrameworkProfile.Avalonia12, solution.Find(twelve)!.Framework.Profile);
        Assert.Equal(FrameworkProfile.Avalonia11, solution.Find(eleven)!.Framework.Profile);
        Assert.All(solution.Projects, p => Assert.True(p.IsDesignable));
        Assert.Equal(solution.Find(twelve), solution.DefaultStartup); // first in the solution
    }
}
