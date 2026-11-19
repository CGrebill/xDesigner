using System.Reflection;

namespace XDesigner.RenderHost.Tests;

/// <summary>Locates build outputs of other projects in the repository.</summary>
internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>Debug or Release, matching how the tests themselves were built.</summary>
    public static string Configuration { get; } =
        typeof(TestPaths).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

    public static string SampleAssembly =>
        Path.Combine(RepoRoot, "samples", "SampleAvaloniaApp", "bin", Configuration, "net10.0", "SampleAvaloniaApp.dll");

    public static string RenderHostExe =>
        Path.Combine(RepoRoot, "src", "XDesigner.RenderHost", "bin", Configuration, "net10.0", "win-x64", "XDesigner.RenderHost.exe");

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
