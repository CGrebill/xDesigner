using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace XDesigner.Core.Project;

/// <summary>What Project ▸ Add creates (§11.5).</summary>
public enum ItemKind
{
    Window,
    UserControl,
}

/// <summary>
/// New projects and new items from the templates embedded in Core (§11.4, §11.5). No dependency on <c>dotnet new</c>
/// or the Avalonia templates being installed: the files are written directly with their tokens replaced.
/// </summary>
public static class ProjectTemplates
{
    /// <summary>The Avalonia version new projects reference (the one the designer's renderer uses).</summary>
    public const string AvaloniaVersion = "12.1.6";

    private const string Prefix = "XDesigner.Core.Templates/";

    /// <summary>Why a project name can't be used, or null. Letters, digits, <c>.</c>, <c>_</c> and <c>-</c>; starts with a letter or <c>_</c>.</summary>
    public static string? ValidateProjectName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a name for the project.";
        }

        if (!(char.IsLetter(name[0]) || name[0] == '_'))
        {
            return "The name must start with a letter.";
        }

        if (name.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-')) || name.EndsWith('.') || name.Contains("..", StringComparison.Ordinal))
        {
            return "Use only letters, digits, dots, hyphens and underscores (no spaces).";
        }

        // The project's id must not be a package it references (NuGet reports a cycle), and no part of its namespace may
        // be a type the template uses unqualified (the namespace would hide the type, CS0118).
        var first = name.Split('.')[0];
        if (ReservedProjectPrefixes.Any(p => first.Equals(p, StringComparison.OrdinalIgnoreCase)))
        {
            return $"A project can't be called {first}: that's the name of a package it uses. Try {first}App.";
        }

        if (NamespaceFor(name).Split('.').FirstOrDefault(TemplateTypeNames.Contains) is { } clash)
        {
            return $"A project can't be called {clash}: that's a type name the new project uses. Try {clash}App or My{clash}.";
        }

        return null;
    }

    /// <summary>The packages new projects reference are all <c>Avalonia</c> or <c>Avalonia.*</c>.</summary>
    private static readonly string[] ReservedProjectPrefixes = ["Avalonia"];

    /// <summary>
    /// Types the templates use without a namespace and don't define themselves: a namespace segment with one of these
    /// names hides the type. (App, MainWindow and Program are defined in the project's namespace, so they still resolve.)
    /// </summary>
    private static readonly HashSet<string> TemplateTypeNames = new(StringComparer.Ordinal)
    {
        "Application", "Window", "UserControl", "AppBuilder", "AvaloniaXamlLoader", "IClassicDesktopStyleApplicationLifetime", "STAThread",
    };

    /// <summary>A C# namespace for a project name: <c>My-App.Tools</c> → <c>My_App.Tools</c>.</summary>
    public static string NamespaceFor(string name) =>
        string.Join('.', name.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(Identifier));

    /// <summary>
    /// Creates <c>{parent}/{name}/{name}.csproj</c> with Program, App, MainWindow, manifest, Assets, global.json and .gitignore.
    /// Returns the project path. Fails if the folder exists and isn't empty.
    /// </summary>
    public static string CreateApp(string parentDirectory, string name, string title)
    {
        if (ValidateProjectName(name) is { } problem)
        {
            throw new ProjectException(problem);
        }

        var dir = Path.Combine(Path.GetFullPath(parentDirectory), name);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
        {
            throw new ProjectException($"The folder {dir} already exists and isn't empty.");
        }

        var tokens = new Dictionary<string, string>
        {
            ["$rootnamespace$"] = NamespaceFor(name),
            // An attribute value: a leading '{' would make it a markup extension, so it gets the {} escape.
            ["$title$"] = System.Security.SecurityElement.Escape(
                Properties.XamlValueSerializer.ToAttribute(string.IsNullOrWhiteSpace(title) ? name : title.Trim())),
            ["$avaloniaversion$"] = AvaloniaVersion,
        };

        Directory.CreateDirectory(Path.Combine(dir, "Assets"));
        foreach (var (file, text) in Templates("AvaloniaApp/"))
        {
            var target = file switch
            {
                "Project.csproj.template" => $"{name}.csproj",
                "global.json.template" => "global.json",
                "gitignore" => ".gitignore",
                _ => file,
            };
            Write(Path.Combine(dir, target), Replace(text, tokens));
        }

        return Path.Combine(dir, $"{name}.csproj");
    }

    /// <summary>Why a class name can't be used for a new Window/UserControl in <paramref name="folder"/>, or null.</summary>
    public static string? ValidateItemName(string folder, string className)
    {
        if (!SyntaxFacts.IsValidIdentifier(className) || SyntaxFacts.GetKeywordKind(className) != SyntaxKind.None)
        {
            return "Enter a C# class name (letters, digits and _; starts with a letter).";
        }

        if (className is "Window" or "UserControl")
        {
            return $"A class can't be called {className}: it would derive from itself. Try My{className}.";
        }

        return File.Exists(Path.Combine(folder, className + ".axaml")) || File.Exists(Path.Combine(folder, className + ".axaml.cs"))
            ? $"{className}.axaml already exists in that folder."
            : null;
    }

    /// <summary>
    /// Creates <c>{folder}/{className}.axaml</c> and its code-behind. The namespace is the project's root namespace plus the
    /// folders below the project (<c>Views/Dialogs</c> → <c>App.Views.Dialogs</c>), as the .NET templates do. Returns the XAML path.
    /// </summary>
    public static string CreateItem(ProjectInfo project, string folder, string className, ItemKind kind)
    {
        if (ValidateItemName(folder, className) is { } problem)
        {
            throw new ProjectException(problem);
        }

        var relative = Path.GetRelativePath(project.Directory, Path.GetFullPath(folder));
        var ns = relative is "." || relative.StartsWith("..", StringComparison.Ordinal)
            ? project.RootNamespace
            : project.RootNamespace + "." + string.Join('.', relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Select(Identifier));
        var tokens = new Dictionary<string, string> { ["$namespace$"] = ns, ["$classname$"] = className };

        Directory.CreateDirectory(folder);
        string? xaml = null;
        foreach (var (file, text) in Templates("Items/").Where(t => t.File.StartsWith(kind + ".", StringComparison.Ordinal)))
        {
            var target = Path.Combine(folder, className + file[kind.ToString().Length..]); // Window.axaml.cs → Name.axaml.cs
            Write(target, Replace(text, tokens));
            if (target.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            {
                xaml = target;
            }
        }

        return xaml!;
    }

    private static string Identifier(string part)
    {
        var cleaned = new string(part.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        return cleaned.Length == 0 || char.IsDigit(cleaned[0]) || SyntaxFacts.GetKeywordKind(cleaned) != SyntaxKind.None ? "_" + cleaned : cleaned;
    }

    /// <summary>Embedded template files under a folder: (file name, text).</summary>
    private static IEnumerable<(string File, string Text)> Templates(string folder)
    {
        var assembly = typeof(ProjectTemplates).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var path = resource.Replace('\\', '/');
            if (!path.StartsWith(Prefix + folder, StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            yield return (path[(Prefix + folder).Length..], reader.ReadToEnd());
        }
    }

    private static string Replace(string text, Dictionary<string, string> tokens)
    {
        foreach (var (token, value) in tokens)
        {
            text = text.Replace(token, value, StringComparison.Ordinal);
        }

        return text.ReplaceLineEndings("\r\n");
    }

    private static void Write(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: path.EndsWith(".cs", StringComparison.Ordinal)));
}
