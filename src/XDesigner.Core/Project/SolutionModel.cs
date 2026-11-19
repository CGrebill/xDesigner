using XDesigner.Core.Frameworks;

namespace XDesigner.Core.Project;

/// <summary>One project of an open solution, with the framework it uses and its XAML files.</summary>
public sealed record SolutionProject(ProjectInfo Info, DetectedFramework Framework, IReadOnlyList<string> XamlFiles)
{
    /// <summary>Has XAML files in a framework this version of the designer can render.</summary>
    public bool IsDesignable => XamlFiles.Count > 0 && Framework.Profile is { IsSupported: true };

    /// <summary>An application (something Run can start), not a library.</summary>
    public bool IsApp => Info.OutputType is "WinExe" or "Exe";

    /// <summary>"App (Avalonia 12)".</summary>
    public string Label => Framework.Profile is { } p ? $"{Info.Name} ({p.DisplayName})" : Info.Name;
}

/// <summary>
/// What the designer has open (§11.1): a solution and all its projects, or one project on its own. Every project keeps
/// its own framework, so a solution may mix Avalonia 11 and 12 (and later WinUI and MAUI) projects; each gets its own
/// renderer.
/// </summary>
public sealed class SolutionModel
{
    private SolutionModel(string? solutionPath, IReadOnlyList<SolutionProject> projects)
    {
        SolutionPath = solutionPath;
        Projects = projects;
    }

    /// <summary>The <c>.sln</c>/<c>.slnx</c>, or null when a single project was opened.</summary>
    public string? SolutionPath { get; }

    public IReadOnlyList<SolutionProject> Projects { get; }

    public string Name => Path.GetFileNameWithoutExtension(SolutionPath ?? (Projects.Count > 0 ? Projects[0].Info.ProjectPath : ""));

    /// <summary>What Build builds: the whole solution, or the single project.</summary>
    public string BuildTarget => SolutionPath ?? Projects[0].Info.ProjectPath;

    public IEnumerable<SolutionProject> DesignableProjects => Projects.Where(p => p.IsDesignable);

    /// <summary>The project Run starts when the user hasn't chosen: the first designable app, else the first designable project.</summary>
    public SolutionProject? DefaultStartup =>
        Projects.FirstOrDefault(p => p.IsDesignable && p.IsApp) ?? Projects.FirstOrDefault(p => p.IsDesignable) ?? (Projects.Count > 0 ? Projects[0] : null);

    /// <summary>The project whose folder contains <paramref name="filePath"/> (the innermost one, for nested projects).</summary>
    public SolutionProject? ProjectContaining(string filePath) =>
        Projects.Where(p => p.Info.Contains(filePath)).MaxBy(p => p.Info.Directory.Length);

    public SolutionProject? Find(string projectPath) =>
        Projects.FirstOrDefault(p => string.Equals(p.Info.ProjectPath, Path.GetFullPath(projectPath), StringComparison.OrdinalIgnoreCase));

    /// <summary>The same solution with one project re-read (after a build: its output and framework may have changed).</summary>
    public SolutionModel With(SolutionProject project) =>
        new(SolutionPath, Projects.Select(p => string.Equals(p.Info.ProjectPath, project.Info.ProjectPath, StringComparison.OrdinalIgnoreCase) ? project : p).ToList());

    /// <summary>
    /// Opens a <c>.sln</c>, <c>.slnx</c> or <c>.csproj</c>: every C# project is evaluated with MSBuild (in parallel).
    /// Projects MSBuild can't read are left out and reported through <paramref name="problems"/>.
    /// </summary>
    public static async Task<SolutionModel> OpenAsync(string path, Action<string>? problems = null, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        var isSolution = path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
        var projectPaths = isSolution ? ProjectReader.SolutionProjects(path) : [path];

        using var throttle = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 2));
        var reads = projectPaths.Select(async projectPath =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var info = await ProjectReader.ReadAsync(projectPath, cancellationToken).ConfigureAwait(false);
                return Describe(info);
            }
            catch (Exception ex) when (ex is ProjectException or System.ComponentModel.Win32Exception or IOException)
            {
                problems?.Invoke($"{Path.GetFileName(projectPath)}: {ex.Message}");
                return null;
            }
            finally
            {
                throttle.Release();
            }
        }).ToList();

        var projects = (await Task.WhenAll(reads).ConfigureAwait(false)).OfType<SolutionProject>().ToList();
        if (!isSolution && projects.Count == 0)
        {
            throw new ProjectException($"MSBuild couldn't read {Path.GetFileName(path)}.");
        }

        return new SolutionModel(isSolution ? path : null, projects);
    }

    /// <summary>A project's framework and XAML files, as they are on disk now.</summary>
    public static SolutionProject Describe(ProjectInfo info) =>
        new(info, info.DetectFramework(), ProjectReader.XamlFiles(info.Directory));

    /// <summary>A solution of projects already read (tests, and projects created by the designer).</summary>
    public static SolutionModel Of(string? solutionPath, IReadOnlyList<SolutionProject> projects) => new(solutionPath, projects);
}
