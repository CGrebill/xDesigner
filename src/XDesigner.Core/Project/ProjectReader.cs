using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace XDesigner.Core.Project;

/// <summary>An opened project (§11.1), as MSBuild evaluates it.</summary>
/// <param name="TargetPath">The built assembly (<c>bin/Debug/net10.0/App.dll</c>); may not exist yet.</param>
/// <param name="Platform">The platform it is built for (<c>x64</c>), for projects that need one (WinUI has no AnyCPU build); else null.</param>
/// <param name="IsPackaged">A packaged (MSIX) WinUI or MAUI app: it runs from a registered package, not from its exe (§11.3).</param>
/// <param name="BuildFramework">The target framework to build and run (<c>-f</c>) when the project targets several: MAUI's Windows one.</param>
public sealed record ProjectInfo(
    string ProjectPath,
    string Name,
    string AssemblyName,
    string RootNamespace,
    string TargetPath,
    string TargetFramework,
    string OutputType,
    string? Platform = null,
    bool IsPackaged = false,
    string? BuildFramework = null)
{
    public string Directory => Path.GetDirectoryName(ProjectPath)!;

    /// <summary>MSBuild's <c>UseWinUI</c> (from the project, its imports or <c>Directory.Build.props</c>).</summary>
    public bool UseWinUI { get; init; }

    /// <summary>MSBuild's <c>UseMaui</c>.</summary>
    public bool UseMaui { get; init; }

    /// <summary>
    /// Package id → version of the project's package references as MSBuild evaluated them (central package management and
    /// properties resolved); null when the project wasn't evaluated (a project read from its file alone).
    /// </summary>
    public IReadOnlyDictionary<string, string>? PackageVersions { get; init; }

    /// <summary>For documents of this project: the assembly the renderer should load, if it has been built.</summary>
    public ProjectOutput ToOutput()
    {
        var built = File.Exists(TargetPath) ? TargetPath : null;
        return new(ProjectPath, AssemblyName, built, DetectFramework().Profile);
    }

    /// <summary>The framework the project uses: from its build when it has one, else from MSBuild's evaluation (§11.1).</summary>
    public Frameworks.DetectedFramework DetectFramework() => Frameworks.FrameworkDetector.Detect(this);

    public bool Contains(string filePath) =>
        Path.GetFullPath(filePath).StartsWith(Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads projects and solutions (§11.1): properties via <c>dotnet msbuild -getProperty</c>, files, solution entries.</summary>
public static partial class ProjectReader
{
    private static readonly string[] Properties =
        ["TargetPath", "AssemblyName", "RootNamespace", "TargetFramework", "TargetFrameworks", "OutputType", "Platform", "UseWinUI", "UseMaui", "WindowsPackageType"];

    /// <summary>
    /// The platform WinUI projects without one of their own are built for here (WinUI has no AnyCPU build): x64, the
    /// designer's renderer's, which can't load an ARM64 build (it runs emulated on ARM64 Windows, as the app then does; review M25).
    /// </summary>
    public static string NativePlatform => "x64";

    /// <summary>
    /// Evaluates the project with MSBuild (no build needed). A multi-targeted project is evaluated for its first desktop
    /// framework (one without a platform suffix such as <c>-android</c>), falling back to its first framework. A WinUI
    /// project without a platform of its own is evaluated for <see cref="NativePlatform"/>, as it is built.
    /// </summary>
    public static async Task<ProjectInfo> ReadAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        projectPath = Path.GetFullPath(projectPath);
        var evaluation = await EvaluateAsync(projectPath, [], cancellationToken).ConfigureAwait(false);
        var values = evaluation.Properties;
        var multiTargeted = false;
        if (string.IsNullOrEmpty(values.GetValueOrDefault("TargetFramework")) && values.GetValueOrDefault("TargetFrameworks") is { Length: > 0 } frameworks)
        {
            var list = frameworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            multiTargeted = list.Length > 1;

            // MAUI: its Windows framework (a plain netX.Y beside it is the unit-test target, review M7). Others: a desktop
            // framework, plain netX.Y, else the Windows one; never Android or iOS first.
            var isMauiProject = string.Equals(values.GetValueOrDefault("UseMaui"), "true", StringComparison.OrdinalIgnoreCase);
            var windows = list.FirstOrDefault(f => f.Contains("-windows", StringComparison.OrdinalIgnoreCase));
            var pick = (isMauiProject ? windows : null)
                ?? list.FirstOrDefault(f => !f.Contains('-'))
                ?? windows
                ?? list[0];
            evaluation = await EvaluateAsync(projectPath, [$"-p:TargetFramework={pick}"], cancellationToken).ConfigureAwait(false);
            values = evaluation.Properties;
        }

        string? platform = null;
        var isMaui = string.Equals(values.GetValueOrDefault("UseMaui"), "true", StringComparison.OrdinalIgnoreCase);
        // A MAUI project is built and run for its Windows target only (no Android or iOS workloads needed), with MAUI's platform handling.
        var isWinUI = !isMaui && string.Equals(values.GetValueOrDefault("UseWinUI"), "true", StringComparison.OrdinalIgnoreCase);
        if (isWinUI)
        {
            platform = values.GetValueOrDefault("Platform") is { } own && !string.Equals(own, "AnyCPU", StringComparison.OrdinalIgnoreCase) && own.Length > 0 ? own : NativePlatform;
            if (!string.Equals(values.GetValueOrDefault("Platform"), platform, StringComparison.OrdinalIgnoreCase))
            {
                string[] extra = values.GetValueOrDefault("TargetFramework") is { Length: > 0 } tfm
                    ? [$"-p:TargetFramework={tfm}", $"-p:Platform={platform}"]
                    : [$"-p:Platform={platform}"];
                evaluation = await EvaluateAsync(projectPath, extra, cancellationToken).ConfigureAwait(false);
                values = evaluation.Properties;
            }
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
            values.GetValueOrDefault("OutputType") ?? "",
            platform,
            (isWinUI || isMaui) && values.GetValueOrDefault("OutputType") is "WinExe" or "Exe"
                && !string.Equals(values.GetValueOrDefault("WindowsPackageType"), "None", StringComparison.OrdinalIgnoreCase),
            // Every multi-targeted project builds and runs one framework: dotnet run refuses to pick (review M7).
            (isMaui || multiTargeted) && values.GetValueOrDefault("TargetFramework") is { Length: > 0 } framework ? framework : null)
        {
            UseWinUI = string.Equals(values.GetValueOrDefault("UseWinUI"), "true", StringComparison.OrdinalIgnoreCase),
            UseMaui = isMaui,
            PackageVersions = evaluation.Packages,
        };
    }

    /// <summary>What MSBuild evaluated: the properties asked for, and package id → version of the package references.</summary>
    internal sealed record Evaluation(Dictionary<string, string> Properties, Dictionary<string, string> Packages);

    private static async Task<Evaluation> EvaluateAsync(string projectPath, string[] extra, CancellationToken cancellationToken)
    {
        var args = new List<string> { "msbuild", projectPath, "-nologo" };
        args.AddRange(Properties.Select(p => "-getProperty:" + p));
        // The packages too: their versions may come from Directory.Build.props or central package management (review M6).
        args.AddRange(["-getItem:PackageReference", "-getItem:PackageVersion"]);
        args.AddRange(extra);
        var result = await ProcessRunner.RunAsync("dotnet", args, Path.GetDirectoryName(projectPath)!, null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new ProjectException($"MSBuild couldn't read {Path.GetFileName(projectPath)}:{Environment.NewLine}{result.Output.Trim()}");
        }

        try
        {
            // stdout alone: evaluation warnings go to stderr and would break the JSON (review L15).
            return ParseEvaluation(result.StandardOutput.Length > 0 ? result.StandardOutput : result.Output);
        }
        catch (JsonException ex)
        {
            throw new ProjectException($"MSBuild's answer about {Path.GetFileName(projectPath)} couldn't be read ({ex.Message}):{Environment.NewLine}{result.Output.Trim()}");
        }
    }

    /// <summary>Parses <c>-getProperty</c> output: JSON <c>{"Properties": {...}}</c> for several properties.</summary>
    internal static Dictionary<string, string> ParseProperties(string output) => ParseEvaluation(output).Properties;

    /// <summary>
    /// Parses <c>-getProperty</c>/<c>-getItem</c> output: <c>{"Properties": {...}, "Items": {"PackageReference": [...]}}</c>.
    /// A package's version is its <c>VersionOverride</c>, its <c>Version</c>, or the central <c>PackageVersion</c>'s.
    /// </summary>
    internal static Evaluation ParseEvaluation(string output)
    {
        var start = output.IndexOf('{');
        if (start < 0)
        {
            throw new ProjectException($"Unexpected MSBuild output: {output.Trim()}");
        }

        using var json = JsonDocument.Parse(output[start..]);
        var properties = json.RootElement.GetProperty("Properties").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);

        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (json.RootElement.TryGetProperty("Items", out var items))
        {
            static IEnumerable<JsonElement> Of(JsonElement items, string name) =>
                items.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];
            static string? Text(JsonElement item, string name) =>
                item.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text ? text : null;

            var central = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var version in Of(items, "PackageVersion"))
            {
                if (Text(version, "Identity") is { } id && Text(version, "Version") is { } v)
                {
                    central[id] = v;
                }
            }

            foreach (var reference in Of(items, "PackageReference"))
            {
                if (Text(reference, "Identity") is { } id
                    && (Text(reference, "VersionOverride") ?? Text(reference, "Version") ?? central.GetValueOrDefault(id)) is { } v)
                {
                    packages[id] = v;
                }
            }
        }

        return new Evaluation(properties, packages);
    }

    /// <summary>
    /// The project's XAML files (<c>.axaml</c> and <c>.xaml</c>: Windows, Pages, UserControls…), skipping <c>bin</c>,
    /// <c>obj</c> and hidden folders, and MAUI's platform folder: <c>Platforms\Windows\App.xaml</c> is a WinUI file (review M8).
    /// </summary>
    public static IReadOnlyList<string> XamlFiles(string projectDirectory)
    {
        var platforms = Path.Combine(Path.GetFullPath(projectDirectory), "Platforms") + Path.DirectorySeparatorChar;
        return Files(projectDirectory, "*.axaml", "*.xaml").Where(f => !f.StartsWith(platforms, StringComparison.OrdinalIgnoreCase)).ToList();
    }

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

    [GeneratedRegex("""^Project\("\{[^}]+\}"\)\s*=\s*"[^"]*"\s*,\s*"(?<path>[^"]+)"\s*,""", RegexOptions.Multiline)]
    private static partial Regex SlnProjectLine();
}

public sealed class ProjectException(string message) : Exception(message);
