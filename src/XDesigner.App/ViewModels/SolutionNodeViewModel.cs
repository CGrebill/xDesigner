using XDesigner.Core.Project;
using Microsoft.UI.Text;
using Windows.UI.Text;

namespace XDesigner.App.ViewModels;

public enum SolutionNodeKind
{
    Solution,
    Project,
    Folder,
    XamlFile,
    CodeFile,
}

/// <summary>One row of Solution Explorer: the solution, a project, a folder or a file.</summary>
public sealed class SolutionNodeViewModel
{
    private SolutionNodeViewModel(SolutionNodeKind kind, string label, string path, SolutionProject? project, string detail = "")
    {
        Kind = kind;
        Label = label;
        Path = path;
        Project = project;
        Detail = detail;
    }

    public SolutionNodeKind Kind { get; }

    public string Label { get; }

    /// <summary>The project's framework and "startup" (<c>Avalonia 12, startup</c>), a solution's project count, or nothing.</summary>
    public string Detail { get; private init; }

    /// <summary>What a screen reader says for the row: the name and its detail (review L51).</summary>
    public string AccessibleName => Detail.Length == 0 ? Label : $"{Label}, {Detail}";

    /// <summary>The file or folder (or project file, or solution file).</summary>
    public string Path { get; }

    /// <summary>The project this node is in (null for the solution itself).</summary>
    public SolutionProject? Project { get; }

    public List<SolutionNodeViewModel> Children { get; } = [];

    public bool IsExpanded { get; set; }

    public bool IsStartup { get; private init; }

    public FontWeight Weight => IsStartup ? FontWeights.SemiBold : FontWeights.Normal;

    public string Glyph => Kind switch
    {
        SolutionNodeKind.Solution => "",
        SolutionNodeKind.Project => "",
        SolutionNodeKind.Folder => "",
        SolutionNodeKind.XamlFile => "",
        _ => "",
    };

    public override string ToString() => Label;

    /// <summary>
    /// The tree for a solution: the solution (when there is one), its projects (startup in bold, with their framework),
    /// their folders, and their XAML and C# files, a code-behind file under its XAML file. <paramref name="expanded"/>
    /// keeps rows open across rebuilds of the tree; <paramref name="collapsed"/> keeps the ones the user closed (the solution
    /// and the startup project start open) closed (review L18).
    /// </summary>
    public static IReadOnlyList<SolutionNodeViewModel> Build(SolutionModel solution, string? startupProjectPath, ISet<string> expanded, ISet<string>? collapsed = null)
    {
        collapsed ??= new HashSet<string>();
        var projects = solution.Projects.Select(p => ProjectNode(p, startupProjectPath, expanded, collapsed)).ToList();
        if (solution.SolutionPath is not { } path)
        {
            return projects;
        }

        var root = new SolutionNodeViewModel(SolutionNodeKind.Solution, $"Solution '{solution.Name}'", path, null,
            $"{solution.Projects.Count} project{(solution.Projects.Count == 1 ? "" : "s")}") { IsExpanded = !collapsed.Contains(path) };
        root.Children.AddRange(projects);
        return [root];
    }

    private static SolutionNodeViewModel ProjectNode(SolutionProject project, string? startupProjectPath, ISet<string> expanded, ISet<string> collapsed)
    {
        var isStartup = string.Equals(project.Info.ProjectPath, startupProjectPath, StringComparison.OrdinalIgnoreCase);
        var detail = string.Join(", ", new[] { project.Framework.Profile?.DisplayName, isStartup ? "startup" : null }.OfType<string>());
        var node = new SolutionNodeViewModel(SolutionNodeKind.Project, project.Info.Name, project.Info.ProjectPath, project, detail)
        {
            IsStartup = isStartup,
            IsExpanded = !collapsed.Contains(project.Info.ProjectPath) && (isStartup || expanded.Contains(project.Info.ProjectPath)),
        };

        var folders = new Dictionary<string, SolutionNodeViewModel>(StringComparer.OrdinalIgnoreCase) { [project.Info.Directory] = node };
        SolutionNodeViewModel FolderFor(string directory)
        {
            if (folders.TryGetValue(directory, out var folder))
            {
                return folder;
            }

            var parent = FolderFor(System.IO.Path.GetDirectoryName(directory)!);
            folder = new SolutionNodeViewModel(SolutionNodeKind.Folder, System.IO.Path.GetFileName(directory), directory, project) { IsExpanded = expanded.Contains(directory) };
            parent.Children.Add(folder);
            folders[directory] = folder;
            return folder;
        }

        var files = ProjectReader.SourceFiles(project.Info.Directory);
        var xaml = files.Where(IsXaml).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nodes = new Dictionary<string, SolutionNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.OrderBy(f => f.Count(c => c == System.IO.Path.DirectorySeparatorChar)).ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var kind = IsXaml(file) ? SolutionNodeKind.XamlFile : SolutionNodeKind.CodeFile;
            var fileNode = new SolutionNodeViewModel(kind, System.IO.Path.GetFileName(file), file, project);
            nodes[file] = fileNode;

            // MainWindow.axaml.cs goes under MainWindow.axaml.
            var owner = file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? file[..^3] : null;
            if (owner is not null && xaml.Contains(owner))
            {
                (nodes.TryGetValue(owner, out var ownerNode) ? ownerNode : FolderFor(System.IO.Path.GetDirectoryName(file)!)).Children.Add(fileNode);
            }
            else
            {
                FolderFor(System.IO.Path.GetDirectoryName(file)!).Children.Add(fileNode);
            }
        }

        Sort(node);
        return node;
    }

    /// <summary>Folders first, then files, each alphabetically (a file's own code-behind stays under it).</summary>
    private static void Sort(SolutionNodeViewModel node)
    {
        var sorted = node.Children.OrderBy(c => c.Kind == SolutionNodeKind.Folder ? 0 : 1).ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();
        node.Children.Clear();
        node.Children.AddRange(sorted);
        foreach (var child in node.Children)
        {
            Sort(child);
        }
    }

    private static bool IsXaml(string path) =>
        path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
}
