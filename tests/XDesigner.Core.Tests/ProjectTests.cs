using XDesigner.Core.Project;

namespace XDesigner.Core.Tests;

public sealed class ProjectTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("xdesigner-project-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void ParseProperties_ReadsMsBuildJson()
    {
        var values = ProjectReader.ParseProperties("""
            {
              "Properties": {
                "TargetPath": "C:\\App\\bin\\Debug\\net10.0\\App.dll",
                "AssemblyName": "App",
                "TargetFrameworks": ""
              }
            }
            """);

        Assert.Equal(@"C:\App\bin\Debug\net10.0\App.dll", values["targetpath"]);
        Assert.Equal("App", values["AssemblyName"]);
    }

    [Fact]
    public void Solutions_ListTheirCSharpProjects()
    {
        var app = Write(@"src\App\App.csproj", "<Project />");
        Write(@"src\Lib\Lib.csproj", "<Project />");
        var sln = Write("All.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Folder", "Folder", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Missing", "src\Missing\Missing.csproj", "{33333333-3333-3333-3333-333333333333}"
            EndProject
            """);
        var slnx = Write("All.slnx", """<Solution><Folder Name="/src/"><Project Path="src/App/App.csproj" /><Project Path="src/Lib/Lib.csproj" /></Folder></Solution>""");

        Assert.Equal([app], ProjectReader.SolutionProjects(sln));
        Assert.Equal(2, ProjectReader.SolutionProjects(slnx).Count);
    }

    [Fact]
    public void XamlFiles_SkipBuildOutput_AndMainWindowIsPreferred()
    {
        Write("App.axaml", "<Application xmlns=\"https://github.com/avaloniaui\" />");
        Write(@"Views\About.axaml", "<Window xmlns=\"https://github.com/avaloniaui\" />");
        Write(@"Views\MainWindow.axaml", "<Window xmlns=\"https://github.com/avaloniaui\" />");
        Write(@"Controls\Card.axaml", "<UserControl xmlns=\"https://github.com/avaloniaui\" />");
        Write(@"obj\Debug\Generated.axaml", "<Window />");
        Write(@"bin\Debug\Copied.axaml", "<Window />");
        Write(@".vs\Cache.axaml", "<Window />");

        var files = ProjectReader.XamlFiles(_dir);
        Assert.Equal(["App.axaml", "Card.axaml", "About.axaml", "MainWindow.axaml"], files.Select(Path.GetFileName));
        Assert.Equal("MainWindow.axaml", Path.GetFileName(ProjectReader.MainXamlFile(files)));
        Assert.Equal("About.axaml", Path.GetFileName(ProjectReader.MainXamlFile(files.Where(f => !f.EndsWith("MainWindow.axaml", StringComparison.Ordinal)).ToList())));
    }

    [Theory]
    [InlineData(@"C:\App\MainWindow.axaml.cs(12,5): error CS1002: ; expected [C:\App\App.csproj]", @"C:\App\MainWindow.axaml.cs", 12, 5, true, "CS1002", "; expected")]
    [InlineData(@"  C:\App\Views\A.axaml(3): error AVLN2000: Unable to resolve type Foo [C:\App\App.csproj]", @"C:\App\Views\A.axaml", 3, null, true, "AVLN2000", "Unable to resolve type Foo")]
    [InlineData(@"C:\App\Program.cs(1,1,1,5): warning CS8019: Unnecessary using directive.", @"C:\App\Program.cs", 1, 1, false, "CS8019", "Unnecessary using directive.")]
    [InlineData("MSBUILD : error MSB1009: Project file does not exist.", null, null, null, true, "MSB1009", "Project file does not exist.")]
    [InlineData(@"C:\Work\App - Copy (2)\MainWindow.axaml.cs(7,9): error CS0103: The name 'x' does not exist [C:\Work\App - Copy (2)\App.csproj]",
        @"C:\Work\App - Copy (2)\MainWindow.axaml.cs", 7, 9, true, "CS0103", "The name 'x' does not exist")]
    [InlineData(@"C:\App\App.csproj : error NU1101: Unable to find package Avalonia. [C:\App\App.csproj]", null, null, null, true, "NU1101", "Unable to find package Avalonia.")]
    [InlineData(@"C:\App\MainWindow.axaml(5,6,5,6): Avalonia error AVLN2000: Unable to resolve type Foo [C:\App\App.csproj]",
        @"C:\App\MainWindow.axaml", 5, 6, true, "AVLN2000", "Unable to resolve type Foo")]
    public void BuildOutput_LinesAreParsed(string line, string? file, int? lineNo, int? col, bool isError, string code, string message)
    {
        var d = BuildRunner.ParseLine(line)!;
        Assert.Equal((file, lineNo, col, isError, code, message), (d.File, d.Line, d.Column, d.IsError, d.Code, d.Message));
    }

    [Theory]
    [InlineData("  App -> C:\\App\\bin\\Debug\\net10.0\\App.dll")]
    [InlineData("Build succeeded.")]
    [InlineData("    0 Error(s)")]
    public void OtherBuildLines_AreNotDiagnostics(string line) => Assert.Null(BuildRunner.ParseLine(line));

    [Fact]
    public void AvaloniaVersion_FromDepsJson_ForLibraries()
    {
        var target = Write(@"bin\Lib.dll", "");
        Write(@"bin\Lib.deps.json", """{ "libraries": { "Lib/1.0.0": {}, "Avalonia/11.2.3": { "type": "package" } } }""");

        Assert.Equal(new Version(11, 2, 3), Frameworks.FrameworkDetector.AvaloniaVersion(target));
        Assert.Null(Frameworks.FrameworkDetector.AvaloniaVersion(Path.Combine(_dir, "nothing", "X.dll")));
    }

    /// <summary>Evaluates the real sample project with MSBuild (needs the dotnet SDK, takes a few seconds).</summary>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task ReadAsync_EvaluatesTheSampleProject()
    {
        var repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "XDesigner.slnx")))
        {
            repo = Path.GetDirectoryName(repo)!;
        }

        var project = await ProjectReader.ReadAsync(Path.Combine(repo, "samples", "SampleAvaloniaApp", "SampleAvaloniaApp.csproj"), TestContext.Current.CancellationToken);

        Assert.Equal("SampleAvaloniaApp", project.AssemblyName);
        Assert.Equal("SampleAvaloniaApp", project.RootNamespace);
        Assert.StartsWith("net10.0", project.TargetFramework);
        Assert.EndsWith(Path.Combine("bin", "Debug", project.TargetFramework, "SampleAvaloniaApp.dll"), project.TargetPath);
        // The evaluated TargetPath is the Debug build; a Release-only build of the solution (CI) has just the Release one.
        var built = Directory.GetFiles(Path.Combine(project.Directory, "bin"), "SampleAvaloniaApp.dll", SearchOption.AllDirectories);
        Assert.NotEmpty(built);
        Assert.Equal(12, Frameworks.FrameworkDetector.AvaloniaVersion(built[0])?.Major);
        Assert.True(project.Contains(Path.Combine(project.Directory, "MainWindow.axaml")));
    }
}
