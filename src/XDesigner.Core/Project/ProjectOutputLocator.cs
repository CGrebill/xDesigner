using System.Xml.Linq;

namespace XDesigner.Core.Project;

/// <summary>The project a XAML file belongs to and its newest built assembly, if any.</summary>
/// <param name="Profile">The framework version the project uses, when it could be detected (§11.1).</param>
public sealed record ProjectOutput(string ProjectPath, string AssemblyName, string? AssemblyPath, Frameworks.FrameworkProfile? Profile = null);

/// <summary>
/// Finds the project that owns a <c>.axaml</c> file and its most recently built assembly, so custom
/// controls render when a file is opened on its own.
/// </summary>
/// <remarks>
/// A quick heuristic (reads the csproj and looks under <c>bin/</c>) for files opened on their own. Files of an opened
/// project use <see cref="ProjectInfo"/> from <see cref="ProjectReader"/> (MSBuild evaluation) instead (§11.1).
/// </remarks>
public static class ProjectOutputLocator
{
    public static ProjectOutput? TryFind(string xamlFilePath)
    {
        for (var dir = new FileInfo(xamlFilePath).Directory; dir is not null; dir = dir.Parent)
        {
            var project = dir.EnumerateFiles("*.csproj").FirstOrDefault();
            if (project is not null)
            {
                return Describe(project);
            }
        }

        return null;
    }

    private static ProjectOutput Describe(FileInfo project)
    {
        var assemblyName = Path.GetFileNameWithoutExtension(project.Name);
        try
        {
            var declared = XDocument.Load(project.FullName).Descendants("AssemblyName").FirstOrDefault()?.Value.Trim();
            if (!string.IsNullOrEmpty(declared) && !declared.Contains("$("))
            {
                assemblyName = declared;
            }
        }
        catch (System.Xml.XmlException)
        {
            // Unreadable project: fall back to the file name.
        }

        var bin = new DirectoryInfo(Path.Combine(project.DirectoryName!, "bin"));
        var newest = bin.Exists
            ? bin.EnumerateFiles(assemblyName + ".dll", SearchOption.AllDirectories)
                .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        return new ProjectOutput(project.FullName, assemblyName, newest?.FullName,
            Frameworks.FrameworkDetector.Detect(project.FullName, newest?.FullName).Profile);
    }
}
