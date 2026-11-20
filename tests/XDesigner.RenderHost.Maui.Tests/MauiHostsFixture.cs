using System.Collections.Concurrent;
using System.Reflection;
using XDesigner.Core.Rendering;
using XDesigner.Protocol.Rendering;

[assembly: AssemblyFixture(typeof(XDesigner.RenderHost.Maui.Tests.MauiHostsFixture))]

namespace XDesigner.RenderHost.Maui.Tests;

/// <summary>Locates build outputs of other projects in the repository.</summary>
internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>Debug or Release, matching how the tests themselves were built.</summary>
    public static string Configuration { get; } =
        typeof(TestPaths).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

    public static string SampleDirectory => Path.Combine(RepoRoot, "samples", "SampleMauiApp");

    private static string Tfm(int major) => $"net{major}.0-windows10.0.19041.0";

    public static string SampleAssembly(int major) =>
        Path.Combine(SampleDirectory, "bin", Configuration, Tfm(major), "win-x64", "SampleMauiApp.dll");

    public static string HostDirectory(int major) => Path.Combine(
        RepoRoot, "src", major == 11 ? "XDesigner.RenderHost.Maui" : "XDesigner.RenderHost.Maui10", "bin", "x64", Configuration, Tfm(major), "win-x64");

    public static string HostName(int major) => $"XDesigner.RenderHost.Maui{major}";

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
/// The MAUI 10 and 11 hosts, each started (on first use) the way the designer starts it for a MAUI project: from a shadow
/// folder of the sample's build for that .NET version, with the project's resource index.
/// </summary>
public sealed class MauiHostsFixture : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, Lazy<Task<(ShadowHostFolder Shadow, RenderHostConnection Host)>>> _hosts = new();

    public static string ShadowRoot { get; } = Path.Combine(Path.GetTempPath(), "xDesigner-tests-maui-shadow");

    /// <summary>The host for MAUI <paramref name="major"/> (10 or 11).</summary>
    public async Task<RenderHostConnection> HostAsync(int major) =>
        (await _hosts.GetOrAdd(major, m => new Lazy<Task<(ShadowHostFolder, RenderHostConnection)>>(() => StartAsync(m))).Value).Host;

    private static async Task<(ShadowHostFolder, RenderHostConnection)> StartAsync(int major)
    {
        var shadow = ShadowHostFolder.Prepare(
            TestPaths.HostDirectory(major), Path.GetDirectoryName(TestPaths.SampleAssembly(major))!, ShadowRoot, ("SampleMauiApp", TestPaths.HostName(major)));
        var host = await RenderHostConnection.StartAsync(new RenderHostStartInfo(
            Path.Combine(shadow.Path, TestPaths.HostName(major) + ".exe"),
            Path.Combine(shadow.Path, "SampleMauiApp.dll"),
            ProjectDirectory: TestPaths.SampleDirectory));
        return (shadow, host);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _hosts.Values)
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
            {
                var (shadow, host) = lazy.Value.Result;
                await host.DisposeAsync();
                shadow.Dispose();
            }
        }
    }
}

internal static class RenderXaml
{
    public const string Namespaces =
        """xmlns="http://schemas.microsoft.com/dotnet/2021/maui" xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml" xmlns:local="clr-namespace:SampleMauiApp.Controls;assembly=SampleMauiApp" """;

    /// <summary>The BGRA pixel at (x, y) of a frame, in DIPs.</summary>
    public static (byte B, byte G, byte R, byte A) Pixel(RenderResult result, double x, double y)
    {
        var i = (((int)(y * result.Scale) * result.PixelWidth) + (int)(x * result.Scale)) * 4;
        return (result.Bgra![i], result.Bgra[i + 1], result.Bgra[i + 2], result.Bgra[i + 3]);
    }

    public static IEnumerable<ElementLayout> All(ElementLayout root) => root.Children.SelectMany(All).Prepend(root);
}
