using System.Reflection;

namespace XDesigner.RenderHost.Tests;

/// <summary>Locates build outputs of other projects in the repository.</summary>
internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>Debug or Release, matching how the tests themselves were built.</summary>
    public static string Configuration { get; } =
        typeof(TestPaths).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

#if AVALONIA11
    /// <summary>The Avalonia major version these tests run against (this file is shared by both host test projects).</summary>
    public const int AvaloniaMajor = 11;
    private const string Sample = "SampleAvalonia11App";
    private const string HostProjectFolder = "XDesigner.RenderHost.Avalonia11";
    private const string HostAssembly = "XDesigner.RenderHost.Avalonia11";
#else
    /// <summary>The Avalonia major version these tests run against (this file is shared by both host test projects).</summary>
    public const int AvaloniaMajor = 12;
    private const string Sample = "SampleAvaloniaApp";
    private const string HostProjectFolder = "XDesigner.RenderHost.Avalonia";
    private const string HostAssembly = "XDesigner.RenderHost.Avalonia12";
#endif

    /// <summary>The sample app's folder. Both samples build an assembly named SampleAvaloniaApp, so the tests are the same.</summary>
    public static string SampleDirectory => Path.Combine(RepoRoot, "samples", Sample);

    public static string SampleAssembly =>
        Path.Combine(SampleDirectory, "bin", Configuration, "net10.0", "SampleAvaloniaApp.dll");

    public static string RenderHostExe =>
        Path.Combine(RepoRoot, "src", HostProjectFolder, "bin", Configuration, "net10.0", "win-x64", HostAssembly + ".exe");

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
