using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace XDesigner.Core.Project;

/// <summary>An opened project (§11.1), as MSBuild evaluates it.</summary>
/// <param name="TargetPath">The built assembly (<c>bin/Debug/net10.0/App.dll</c>); may not exist yet.</param>
public sealed record ProjectInfo(
    string ProjectPath,
    string Name,
    string AssemblyName,
    string RootNamespace,
    string TargetPath,
    string TargetFramework,
    string OutputType)
{
    public string Directory => Path.GetDirectoryName(ProjectPath)!;

    /// <summary>For documents of this project: the assembly the renderer should load, if it has been built.</summary>
    public ProjectOutput ToOutput()
    {
        var built = File.Exists(TargetPath) ? TargetPath : null;
        return new(ProjectPath, AssemblyName, built, Frameworks.FrameworkDetector.Detect(ProjectPath, built).Profile);
    }

    /// <summary>The framework the project uses: from its build when it has one, else from the project file.</summary>
    public Frameworks.DetectedFramework DetectFramework() =>
        Frameworks.FrameworkDetector.Detect(ProjectPath, File.Exists(TargetPath) ? TargetPath : null);

    public bool Contains(string filePath) =>
        Path.GetFullPath(filePath).StartsWith(Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads projects and solutions (§11.1): properties via <c>dotnet msbuild -getProperty</c>, files, solution entries.</summary>
public static partial class ProjectReader
{
    private static readonly string[] Properties = ["TargetPath", "AssemblyName", "RootNamespace", "TargetFramework", "TargetFrameworks", "OutputType"];

    /// <summary>
    /// Evaluates the project with MSBuild (no build needed). A multi-targeted project is evaluated for its first desktop
    /// framework (one without a platform suffix such as <c>-android</c>), falling back to its first framework.
    /// </summary>
    public static async Task<ProjectInfo> ReadAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        projectPath = Path.GetFullPath(projectPath);
        var values = await GetPropertiesAsync(projectPath, [], cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(values.GetValueOrDefault("TargetFramework")) && values.GetValueOrDefault("TargetFrameworks") is { Length: > 0 } frameworks)
        {
            var list = frameworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // A desktop framework: plain netX.Y, else the Windows one (MAUI), never Android or iOS first.
            var pick = list.FirstOrDefault(f => !f.Contains('-'))
                ?? list.FirstOrDefault(f => f.Contains("-windows", StringComparison.OrdinalIgnoreCase))
                ?? list[0];
            values = await GetPropertiesAsync(projectPath, [$"-p:TargetFramework={pick}"], cancellationToken).ConfigureAwait(false);
        }

        var name = Path.GetFileNameWithoutExtension(projectPath);
        var assemblyName = values.GetValueOrDefault("AssemblyName") is { Length: > 0 } a ? a : name;
        return new ProjectInfo(
            projectPath,
            name,
            assemblyName,
            values.GetValueOrDefault("RootNamespace") is { Length: > 0 } ns ? ns : assemblyName,
            values.GetValueOrDefault("TargetPath") ?? "",
            values.GetValueOrDefault("TargetFramework") ?? "",
            values.GetValueOrDefault("OutputType") ?? "");
    }

    private static async Task<Dictionary<string, string>> GetPropertiesAsync(string projectPath, string[] extra, CancellationToken cancellationToken)
    {
        var args = new List<string> { "msbuild", projectPath, "-nologo" };
        args.AddRange(Properties.Select(p => "-getProperty:" + p));
        args.AddRange(extra);
        var result = await ProcessRunner.RunAsync("dotnet", args, Path.GetDirectoryName(projectPath)!, null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new ProjectException($"MSBuild couldn't read {Path.GetFileName(projectPath)}:{Environment.NewLine}{result.Output.Trim()}");
        }

        return ParseProperties(result.Output);
    }

    /// <summary>Parses <c>-getProperty</c> output: JSON <c>{"Properties": {...}}</c> for several properties.</summary>
    internal static Dictionary<string, string> ParseProperties(string output)
    {
        var start = output.IndexOf('{');
        if (start < 0)
        {
            throw new ProjectException($"Unexpected MSBuild output: {output.Trim()}");
        }

        using var json = JsonDocument.Parse(output[start..]);
        return json.RootElement.GetProperty("Properties").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The project's XAML files (<c>.axaml</c> and <c>.xaml</c>: Windows, Pages, UserControls…), skipping <c>bin</c>,
    /// <c>obj</c> and hidden folders.
    /// </summary>
    public static IReadOnlyList<string> XamlFiles(string projectDirectory) => Files(projectDirectory, "*.axaml", "*.xaml");

    /// <summary>The project's XAML and C# files (what Solution Explorer lists), skipping <c>bin</c>, <c>obj</c> and hidden folders.</summary>
    public static IReadOnlyList<string> SourceFiles(string projectDirectory) => Files(projectDirectory, "*.axaml", "*.xaml", "*.cs");

    private static List<string> Files(string projectDirectory, params string[] patterns)
    {
        var result = new List<string>();
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(projectDirectory)]);
        while (pending.TryPop(out var dir))
        {
            try
            {
                result.AddRange(patterns.SelectMany(dir.EnumerateFiles).Select(f => f.FullName));
                foreach (var sub in dir.EnumerateDirectories())
                {
                    if (sub.Name is not ("bin" or "obj" or "node_modules") && !sub.Name.StartsWith('.'))
                    {
                        pending.Push(sub);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip folders that can't be read.
            }
        }

        return result.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The file a project opens with: <c>MainWindow</c>, else <c>MainPage</c>, else the first Window or Page, else the first
    /// file. Never the application itself (<c>App</c>, <c>AppShell</c>).
    /// </summary>
    public static string? MainXamlFile(IReadOnlyList<string> files)
    {
        static string Stem(string path) => Path.GetFileName(path).Split('.')[0];
        var candidates = files.Where(f => Stem(f) is not ("App" or "AppShell")).ToList();
        return candidates.FirstOrDefault(f => Stem(f).Equals("MainWindow", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(f => Stem(f).Equals("MainPage", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(IsWindowOrPage)
            ?? candidates.FirstOrDefault();

        static bool IsWindowOrPage(string path)
        {
            try
            {
                return XDocument.Load(path).Root?.Name.LocalName is "Window" or "Page" or "ContentPage";
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>The C# projects of a <c>.sln</c> or <c>.slnx</c>, as full paths that exist.</summary>
    public static IReadOnlyList<string> SolutionProjects(string solutionPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        IEnumerable<string> relative;
        if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            relative = XDocument.Load(solutionPath).Descendants("Project").Select(p => (string?)p.Attribute("Path")).OfType<string>();
        }
        else
        {
            relative = SlnProjectLine().Matches(File.ReadAllText(solutionPath)).Select(m => m.Groups["path"].Value);
        }

        return relative
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFullPath(Path.Combine(dir, p.Replace('\\', Path.DirectorySeparatorChar))))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The Avalonia version a built project uses: <c>Avalonia.Base.dll</c> in the output folder (apps), else the
    /// <c>Avalonia</c> package listed in its <c>deps.json</c> (libraries). Null when it can't tell.
    /// </summary>
    public static Version? AvaloniaVersionOf(string targetPath) => Frameworks.FrameworkDetector.AvaloniaVersion(targetPath);

    [GeneratedRegex("""^Project\("\{[^}]+\}"\)\s*=\s*"[^"]*"\s*,\s*"(?<path>[^"]+)"\s*,""", RegexOptions.Multiline)]
    private static partial Regex SlnProjectLine();
}

public sealed class ProjectException(string message) : Exception(message);
