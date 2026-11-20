using System.Xml.Linq;
using XDesigner.Core.Frameworks;
using XDesigner.Core.Metadata;
using XDesigner.Core.Project;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;

namespace XDesigner.Core.Tests;

public sealed class FrameworkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("xdesigner-framework-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Theory]
    [InlineData("<UserControl xmlns=\"https://github.com/avaloniaui\" />", FrameworkId.Avalonia12)]
    [InlineData("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" />", FrameworkId.WinUI3)]
    [InlineData("<ContentPage xmlns=\"http://schemas.microsoft.com/dotnet/2021/maui\" />", FrameworkId.Maui11)]
    [InlineData("<Window xmlns=\"urn:unknown\" />", FrameworkId.Avalonia12)] // unknown: the default
    public void Documents_GetTheProfileOfTheirRootNamespace(string xaml, FrameworkId expected) =>
        Assert.Equal(expected, XamlDocument.Parse(xaml).Profile.Id);

    [Fact]
    public void MauiDocuments_UseThe2009XamlNamespaceForNames()
    {
        var doc = XamlDocument.Parse("""
            <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui" xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml">
              <Button x:Name="ok" />
            </ContentPage>
            """);

        Assert.Equal("ok", doc.Root.Children.Single().Name);
        Assert.Equal(FrameworkProfile.Xaml2009Namespace, doc.Profile.XamlNamespace.NamespaceName);
    }

    [Fact]
    public void BuildOutput_TellsTheFrameworkAndVersion()
    {
        var target = Path.Combine(_dir, "bin", "App.dll");
        Write(@"bin\App.deps.json", """{ "libraries": { "Avalonia/11.3.2": {}, "Other/1.0.0": {} } }""");
        Assert.Equal(FrameworkId.Avalonia11, FrameworkDetector.FromBuildOutput(target).Profile?.Id);

        Write(@"bin\App.deps.json", """{ "libraries": { "Microsoft.WindowsAppSDK/2.7.0": {}, "Microsoft.Maui.Controls/10.0.20": {} } }""");
        Assert.Equal(FrameworkId.Maui10, FrameworkDetector.FromBuildOutput(target).Profile?.Id); // MAUI wins over its Windows App SDK

        Write(@"bin\App.deps.json", """{ "libraries": { "Microsoft.WindowsAppSDK/2.7.0": {} } }""");
        Assert.Equal(FrameworkId.WinUI3, FrameworkDetector.FromBuildOutput(target).Profile?.Id);

        Write(@"bin\App.deps.json", """{ "libraries": { "Avalonia/0.10.21": {} } }""");
        var old = FrameworkDetector.FromBuildOutput(target);
        Assert.Equal((FrameworkFamily.Avalonia, (FrameworkProfile?)null), (old.Family, old.Profile));
        Assert.Contains("supports Avalonia 11 and Avalonia 12", old.Problem("App"));
    }

    [Theory]
    [InlineData("<Project><ItemGroup><PackageReference Include=\"Avalonia\" Version=\"11.2.1\" /></ItemGroup></Project>", FrameworkId.Avalonia11)]
    [InlineData("<Project><PropertyGroup><AvaloniaVersion>12.1.6</AvaloniaVersion></PropertyGroup><ItemGroup><PackageReference Include=\"Avalonia\" Version=\"$(AvaloniaVersion)\" /></ItemGroup></Project>", FrameworkId.Avalonia12)]
    [InlineData("<Project><PropertyGroup><UseWinUI>true</UseWinUI></PropertyGroup></Project>", FrameworkId.WinUI3)]
    [InlineData("<Project><PropertyGroup><UseMaui>true</UseMaui><TargetFrameworks>net11.0-android;net11.0-windows10.0.19041.0</TargetFrameworks></PropertyGroup></Project>", FrameworkId.Maui11)]
    public void ProjectFile_TellsTheFrameworkBeforeABuild(string csproj, FrameworkId expected) =>
        Assert.Equal(expected, FrameworkDetector.FromProjectFile(Write("App.csproj", csproj)).Profile?.Id);

    [Fact]
    public void ProjectFile_WithCentralPackageVersions()
    {
        Write("Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"Avalonia\" Version=\"11.3.0\" /></ItemGroup></Project>");
        var csproj = Write(@"src\App\App.csproj", "<Project><ItemGroup><PackageReference Include=\"Avalonia\" /></ItemGroup></Project>");

        Assert.Equal(FrameworkId.Avalonia11, FrameworkDetector.FromProjectFile(csproj).Profile?.Id);
    }

    [Fact]
    public void UnsupportedFrameworks_AreExplained()
    {
        var maui = new DetectedFramework(FrameworkFamily.Maui, new Version(10, 0), FrameworkProfile.Maui10);
        Assert.Contains("coming in a later version", maui.Problem("Shop"));
        Assert.Null(new DetectedFramework(FrameworkFamily.WinUI, null, FrameworkProfile.WinUI3).Problem("Shop"));
        Assert.Null(new DetectedFramework(FrameworkFamily.Avalonia, new Version(12, 1), FrameworkProfile.Avalonia12).Problem("Shop"));
        Assert.Contains("doesn't use a UI framework", DetectedFramework.Unknown.Problem("Shop"));
    }

    [Fact]
    public void Catalog_ChecksBuiltInTypesOfItsFramework()
    {
        ControlTypeInfo T(string fullName, params string[] bases) =>
            new(fullName, fullName[(fullName.LastIndexOf('.') + 1)..], "urn:x", fullName[..fullName.LastIndexOf('.')], "A", true, true, ContentModel.Children, null, null, bases);

        var avaloniaGrid = T("Avalonia.Controls.Grid");
        var winuiGrid = T("Microsoft.UI.Xaml.Controls.Grid");
        Assert.True(new ControlCatalog([avaloniaGrid]).IsA(avaloniaGrid, "Grid"));
        Assert.False(new ControlCatalog([winuiGrid]).IsA(winuiGrid, "Grid")); // an Avalonia catalog
        Assert.True(new ControlCatalog([winuiGrid], FrameworkProfile.WinUI3).IsA(winuiGrid, "Grid"));
    }

    [Fact]
    public void XamlFiles_IncludeXamlAndAxaml_AndTheMainFileIsNeverTheApp()
    {
        Write("App.xaml", "<Application />");
        Write("AppShell.xaml", "<Shell />");
        Write(@"Views\Settings.xaml", "<ContentPage />");
        Write("MainPage.xaml", "<ContentPage />");
        Write("Card.axaml", "<UserControl />");

        var files = ProjectReader.XamlFiles(_dir);

        Assert.Equal(5, files.Count);
        Assert.Equal("MainPage.xaml", Path.GetFileName(ProjectReader.MainXamlFile(files)));
        Assert.Equal("Settings.xaml", Path.GetFileName(ProjectReader.MainXamlFile(files.Where(f => !f.EndsWith("MainPage.xaml", StringComparison.Ordinal)).ToList())));
    }

    [Fact]
    public void Profiles_KnowTheirNamespacesAndHosts()
    {
        Assert.All(FrameworkProfile.All, p => Assert.Equal(p, FrameworkProfile.For(p.Id)));
        Assert.Equal([FrameworkId.Avalonia12, FrameworkId.Avalonia11, FrameworkId.WinUI3], FrameworkProfile.All.Where(p => p.IsSupported).Select(p => p.Id));
        Assert.Equal(FrameworkProfile.Avalonia11, FrameworkProfile.For(FrameworkFamily.Avalonia, 11));
        Assert.Null(FrameworkProfile.For(FrameworkFamily.Avalonia, 10));
        Assert.Equal(FrameworkFamily.Maui, FrameworkProfile.FamilyOf(XNamespace.Get(FrameworkProfile.MauiNamespace)));
        Assert.Equal(FrameworkProfile.All.Count, FrameworkProfile.All.Select(p => p.HostExecutable).Distinct().Count());
    }
}
