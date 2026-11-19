using XDesigner.Core.Project;

namespace XDesigner.Core.Tests;

public sealed class ProjectOutputLocatorTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("xdesigner-locator-");

    [Fact]
    public void FindsOwningProjectAndNewestBuiltAssembly()
    {
        var project = Write("MyApp/MyApp.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>Custom.Name</AssemblyName></PropertyGroup></Project>");
        var xaml = Write("MyApp/Views/Main.axaml", "<Window />");
        var older = Write("MyApp/bin/Release/net10.0/Custom.Name.dll", "");
        var newer = Write("MyApp/bin/Debug/net10.0/Custom.Name.dll", "");
        Write("MyApp/obj/Debug/net10.0/ref/Custom.Name.dll", "");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));

        var output = ProjectOutputLocator.TryFind(xaml);

        Assert.NotNull(output);
        Assert.Equal(project, output.ProjectPath);
        Assert.Equal("Custom.Name", output.AssemblyName);
        Assert.Equal(newer, output.AssemblyPath);
    }

    [Fact]
    public void ProjectNotBuiltYet_HasNoAssemblyPath()
    {
        Write("App/App.csproj", "<Project />");
        var xaml = Write("App/Main.axaml", "<Window />");

        var output = ProjectOutputLocator.TryFind(xaml);

        Assert.Equal("App", output!.AssemblyName);
        Assert.Null(output.AssemblyPath);
    }

    [Fact]
    public void NoProject_ReturnsNull()
    {
        Assert.Null(ProjectOutputLocator.TryFind(Write("loose/Main.axaml", "<Window />")));
    }

    public void Dispose() => _root.Delete(recursive: true);

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_root.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
