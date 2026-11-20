using System.Reflection;
using XDesigner.Core.Rendering;

[assembly: AssemblyFixture(typeof(XDesigner.RenderHost.WinUI.Tests.WinUIHostFixture))]

namespace XDesigner.RenderHost.WinUI.Tests;

/// <summary>Locates build outputs of other projects in the repository.</summary>
internal static class TestPaths
{
    private const string WindowsTfm = "net10.0-windows10.0.19041.0";

    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>Debug or Release, matching how the tests themselves were built.</summary>
    public static string Configuration { get; } =
        typeof(TestPaths).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

    public static string SampleDirectory => Path.Combine(RepoRoot, "samples", "SampleWinUIApp");

    public static string SampleAssembly => Path.Combine(SampleDirectory, "bin", "x64", Configuration, WindowsTfm, "win-x64", "SampleWinUIApp.dll");

    public static string HostDirectory => Path.Combine(RepoRoot, "src", "XDesigner.RenderHost.WinUI", "bin", "x64", Configuration, WindowsTfm, "win-x64");

    public const string HostExecutable = "XDesigner.RenderHost.WinUI.exe";

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XDesigner.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (XDesigner.slnx).");
    }
}

/// <summary>
/// One WinUI host for the test assembly, started the way the designer starts it for a WinUI project: from a shadow folder
/// of the sample's build output, with the sample's assembly and project folder.
/// </summary>
public sealed class WinUIHostFixture : IAsyncLifetime
{
    private ShadowHostFolder? _shadow;

    /// <summary>Where this run's shadow folders go (removed afterwards).</summary>
    public static string ShadowRoot { get; } = Path.Combine(Path.GetTempPath(), "xDesigner-tests-shadow");

    public RenderHostConnection Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _shadow = ShadowHostFolder.Prepare(TestPaths.HostDirectory, Path.GetDirectoryName(TestPaths.SampleAssembly)!, ShadowRoot);
        Host = await RenderHostConnection.StartAsync(new RenderHostStartInfo(
            Path.Combine(_shadow.Path, TestPaths.HostExecutable),
            Path.Combine(_shadow.Path, "SampleWinUIApp.dll"),
            ProjectDirectory: TestPaths.SampleDirectory));
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is not null)
        {
            await Host.DisposeAsync();
        }

        _shadow?.Dispose();
    }
}

/// <summary>Builds render-copy XAML the way the designer's Core does (§5.4).</summary>
internal static class RenderXaml
{
    public const string Namespaces =
        """xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:d="http://schemas.microsoft.com/expression/blend/2008" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" mc:Ignorable="d" xmlns:local="using:SampleWinUIApp.Controls" """;

    /// <summary>The BGRA pixel at (x, y) of a frame, in DIPs.</summary>
    public static (byte B, byte G, byte R, byte A) Pixel(Protocol.Rendering.RenderResult result, double x, double y)
    {
        var px = (int)(x * result.Scale);
        var py = (int)(y * result.Scale);
        var i = ((py * result.PixelWidth) + px) * 4;
        return (result.Bgra![i], result.Bgra[i + 1], result.Bgra[i + 2], result.Bgra[i + 3]);
    }

    public static IEnumerable<Protocol.Rendering.ElementLayout> All(Protocol.Rendering.ElementLayout root) =>
        root.Children.SelectMany(All).Prepend(root);
}
