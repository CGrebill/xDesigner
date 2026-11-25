using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace XDesigner.Core.Project;

/// <summary>What Project ▸ Add creates (§11.5).</summary>
public enum ItemKind
{
    Window,
    UserControl,

    /// <summary>A WinUI Page, or a MAUI ContentPage (Avalonia has no pages).</summary>
    Page,
}

/// <summary>
/// New projects and new items from the templates embedded in Core (§11.4, §11.5). No dependency on <c>dotnet new</c>
/// or the Avalonia templates being installed: the files are written directly with their tokens replaced.
/// </summary>
public static class ProjectTemplates
{
    /// <summary>The framework versions the designer can create new projects for: by framework (Avalonia, WinUI, MAUI), newest first.</summary>
    public static IReadOnlyList<Frameworks.FrameworkProfile> CreatableFrameworks { get; } =
        Frameworks.FrameworkProfile.All.Where(p => p.ProjectTemplate is not null).OrderBy(p => p.Family).ThenByDescending(p => p.MajorVersion).ToList();

    private const string Prefix = "XDesigner.Core.Templates/";

    /// <summary>Why a project name can't be used, or null. Letters, digits, <c>.</c>, <c>_</c> and <c>-</c>; starts with a letter or <c>_</c>.</summary>
    /// <param name="framework">The framework it's created for: each one's template uses other names (all of them when omitted).</param>
    public static string? ValidateProjectName(string name, Frameworks.FrameworkProfile? framework = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a name for the project.";
        }

        if (!(char.IsLetter(name[0]) || name[0] == '_'))
        {
            return "The name must start with a letter or an underscore.";
        }

        if (name.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-')) || name.EndsWith('.') || name.Contains("..", StringComparison.Ordinal))
        {
            return "Use only letters, digits, dots, hyphens and underscores (no spaces).";
        }

        // The project's id must not be a package it references (NuGet reports a cycle), and no part of its namespace may
        // be a type the template uses unqualified (the namespace would hide the type, CS0118).
        var first = name.Split('.')[0];
        if (ReservedPrefixesFor(framework).Any(p => first.Equals(p, StringComparison.OrdinalIgnoreCase)))
        {
            return $"A project can't be called {first}: that's the name of a package it uses. Try {first}App.";
        }

        if (NamespaceFor(name).Split('.').FirstOrDefault(TemplateTypeNamesFor(framework).Contains) is { } clash)
        {
            return $"A project can't be called {clash}: that's a type name the new project uses. Try {clash}App or My{clash}.";
        }

        return null;
    }

    /// <summary>The packages a framework's new projects reference start with these (<c>Avalonia.*</c>, <c>Microsoft.*</c>).</summary>
    private static string[] ReservedPrefixesFor(Frameworks.FrameworkProfile? framework) => framework?.Family switch
    {
        Frameworks.FrameworkFamily.Avalonia => ["Avalonia"],
        Frameworks.FrameworkFamily.WinUI or Frameworks.FrameworkFamily.Maui => ["Microsoft"],
        _ => ["Avalonia", "Microsoft"],
    };

    /// <summary>
    /// Types the templates use without a namespace and don't define themselves: a namespace segment with one of these
    /// names hides the type. (App, MainWindow and Program are defined in the project's namespace, so they still resolve.)
    /// </summary>
    private static HashSet<string> TemplateTypeNamesFor(Frameworks.FrameworkProfile? framework) => framework?.Family switch
    {
        Frameworks.FrameworkFamily.Avalonia => [.. CommonTypeNames, .. AvaloniaTypeNames],
        Frameworks.FrameworkFamily.WinUI => [.. CommonTypeNames, .. WinUITypeNames],
        Frameworks.FrameworkFamily.Maui => [.. CommonTypeNames, .. MauiTypeNames],
        _ => [.. CommonTypeNames, .. AvaloniaTypeNames, .. WinUITypeNames, .. MauiTypeNames],
    };

    private static readonly string[] CommonTypeNames = ["Application", "Window", "STAThread"];
    private static readonly string[] AvaloniaTypeNames = ["UserControl", "AppBuilder", "AvaloniaXamlLoader", "IClassicDesktopStyleApplicationLifetime"];
    private static readonly string[] WinUITypeNames =
        ["Page", "UserControl", "WinRT", "LaunchActivatedEventArgs", "DispatcherQueue", "DispatcherQueueSynchronizationContext", "SynchronizationContext"];
    private static readonly string[] MauiTypeNames = ["MauiApp", "ContentPage", "ContentView", "IActivationState", "AppInfo", "MauiWinUIApplication", "Microsoft"];

    /// <summary>
    /// The item kinds Project ▸ Add offers for a framework. In MAUI, a page is a ContentPage and a user control a ContentView,
    /// and windows are made in code.
    /// </summary>
    public static IReadOnlyList<ItemKind> ItemKindsFor(Frameworks.FrameworkProfile profile) => profile.Family switch
    {
        Frameworks.FrameworkFamily.WinUI => [ItemKind.Window, ItemKind.Page, ItemKind.UserControl],
        Frameworks.FrameworkFamily.Maui => [ItemKind.Page, ItemKind.UserControl],
        _ => [ItemKind.Window, ItemKind.UserControl],
    };

    /// <summary>The embedded item templates of a framework.</summary>
    private static string ItemsFolder(Frameworks.FrameworkProfile profile) => profile.Family switch
    {
        Frameworks.FrameworkFamily.WinUI => "WinUIItems/",
        Frameworks.FrameworkFamily.Maui => "MauiItems/",
        _ => "Items/",
    };

    /// <summary>A C# namespace for a project name: <c>My-App.Tools</c> → <c>My_App.Tools</c>.</summary>
    public static string NamespaceFor(string name) =>
        string.Join('.', name.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(Identifier));

    /// <summary>
    /// Creates <c>{parent}/{name}/{name}.csproj</c> with Program, App, MainWindow, manifest, Assets, global.json and .gitignore.
    /// Returns the project path. Fails if the folder exists and isn't empty.
    /// </summary>
    /// <param name="framework">Which framework version to create it for (Avalonia 12 when omitted).</param>
    /// <param name="packaged">WinUI and MAUI: a packaged (MSIX) app, with a <c>Package.appxmanifest</c> and logos; else an unpackaged one.</param>
    public static string CreateApp(string parentDirectory, string name, string title, Frameworks.FrameworkProfile? framework = null, bool packaged = false)
    {
        framework ??= Frameworks.FrameworkProfile.Avalonia12;
        if (framework.ProjectTemplate is not { } template || framework.TemplateVersion is not { } frameworkVersion)
        {
            throw new ProjectException($"The designer can't create {framework.DisplayName} projects yet.");
        }

        if (ValidateProjectName(name, framework) is { } problem)
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
            ["$avaloniaversion$"] = frameworkVersion,
            ["$windowsappsdkversion$"] = frameworkVersion,
            // WinUI: unpackaged apps say so; packaged ones are what WinUI builds by default.
            ["$packaging$"] = packaged ? "" : "    <WindowsPackageType>None</WindowsPackageType>\r\n",
            ["$packagename$"] = PackageName(name),
            ["$packagetitle$"] = System.Security.SecurityElement.Escape(string.IsNullOrWhiteSpace(title) ? name : title.Trim()),
            // MAUI
            ["$mauiversion$"] = frameworkVersion,
            ["$dotnetversion$"] = (framework.Family == Frameworks.FrameworkFamily.Maui ? framework.MajorVersion : 10).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["$applicationid$"] = ApplicationId(name),
        };

        var assets = Path.Combine(dir, framework.AssetsFolder);
        Directory.CreateDirectory(assets);
        foreach (var (file, text) in Templates(template + "/"))
        {
            if (file.EndsWith("Package.appxmanifest.template", StringComparison.Ordinal) && !packaged)
            {
                continue;
            }

            var target = file switch
            {
                "Project.csproj.template" => $"{name}.csproj",
                "global.json.template" => "global.json",
                "gitignore" => ".gitignore",
                _ when file.EndsWith(".appxmanifest.template", StringComparison.Ordinal) => file[..^".template".Length],
                _ => file,
            };
            var path = Path.Combine(dir, target.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Write(path, Replace(text, tokens));
        }

        // A packaged WinUI app's manifest names these logos (plain squares to replace); MAUI makes its own from MauiIcon.
        if (packaged && framework.Family == Frameworks.FrameworkFamily.WinUI)
        {
            // The logos the package manifest names (plain squares to replace with your own).
            foreach (var (file, size) in new[] { ("StoreLogo.png", 50), ("Square150x150Logo.png", 150), ("Square44x44Logo.png", 44) })
            {
                File.WriteAllBytes(Path.Combine(assets, file), SolidPng(size, 0x00, 0x78, 0xD4));
            }
        }

        return Path.Combine(dir, $"{name}.csproj");
    }

    /// <summary>A MAUI application id: <c>com.companyname.{name}</c> in lowercase letters and digits (<c>My-App</c> → <c>com.companyname.myapp</c>).</summary>
    internal static string ApplicationId(string projectName)
    {
        var name = new string(projectName.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return "com.companyname." + (name.Length > 0 && char.IsAsciiLetter(name[0]) ? name : "app" + name);
    }

    /// <summary>A package identity name: letters, digits, <c>.</c> and <c>-</c>, 3 to 50 characters.</summary>
    internal static string PackageName(string projectName)
    {
        var name = new string(projectName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '-').ToArray()).Trim('.', '-');
        name = name.Length < 3 ? name + "App" : name;
        return name.Length > 50 ? name[..50].TrimEnd('.', '-') : name;
    }

    /// <summary>A square PNG of one colour.</summary>
    internal static byte[] SolidPng(int size, byte red, byte green, byte blue)
    {
        var raw = new byte[size * ((size * 3) + 1)];
        for (var y = 0; y < size; y++)
        {
            var row = y * ((size * 3) + 1); // filter byte 0, then RGB
            for (var x = 0; x < size; x++)
            {
                raw[row + 1 + (x * 3)] = red;
                raw[row + 2 + (x * 3)] = green;
                raw[row + 3 + (x * 3)] = blue;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, size);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8; // bit depth
        header[9] = 2; // truecolour
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);
        var crc = Crc32(typeBytes, Crc32([], 0xFFFFFFFFu, finish: false), finish: false);
        crc = Crc32(data, crc, finish: true);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        png.Write(number);
    }

    private static uint Crc32(ReadOnlySpan<byte> data, uint crc, bool finish)
    {
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return finish ? crc ^ 0xFFFFFFFFu : crc;
    }

    /// <summary>Why a class name can't be used for a new Window/UserControl in <paramref name="folder"/>, or null.</summary>
    public static string? ValidateItemName(string folder, string className, Frameworks.FrameworkProfile? profile = null)
    {
        var extension = (profile ?? Frameworks.FrameworkProfile.Avalonia12).FileExtension;
        if (!SyntaxFacts.IsValidIdentifier(className) || SyntaxFacts.GetKeywordKind(className) != SyntaxKind.None)
        {
            return "Enter a C# class name (letters, digits and _; starts with a letter).";
        }

        if (className is "Window" or "UserControl" or "Page" or "ContentPage" or "ContentView")
        {
            return $"A class can't be called {className}: it would derive from itself. Try My{className}.";
        }

        // Names the project's own startup code uses unqualified (review L8): Program, App, MauiProgram, Application…
        if (className is "Program" or "App" or "MauiProgram" or "Application" or "AppBuilder" or "MauiApp"
            || TemplateTypeNamesFor(profile).Contains(className))
        {
            return $"A class can't be called {className}: the project's startup code already uses that name. Try another, such as {className}View.";
        }

        return File.Exists(Path.Combine(folder, className + extension)) || File.Exists(Path.Combine(folder, className + extension + ".cs"))
            ? $"{className}{extension} already exists in that folder."
            : null;
    }

    /// <summary>
    /// Creates <c>{folder}/{className}.axaml</c> (<c>.xaml</c> in WinUI) and its code-behind. The namespace is the project's
    /// root namespace plus the folders below the project (<c>Views/Dialogs</c> → <c>App.Views.Dialogs</c>), as the .NET
    /// templates do. Returns the XAML path.
    /// </summary>
    /// <param name="profile">The project's framework (Avalonia 12 when omitted).</param>
    public static string CreateItem(ProjectInfo project, string folder, string className, ItemKind kind, Frameworks.FrameworkProfile? profile = null)
    {
        profile ??= Frameworks.FrameworkProfile.Avalonia12;
        if (!ItemKindsFor(profile).Contains(kind))
        {
            throw new ProjectException($"{profile.DisplayName} projects have no {kind} item.");
        }

        if (ValidateItemName(folder, className, profile) is { } problem)
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
        foreach (var (file, text) in Templates(ItemsFolder(profile)).Where(t => t.File.StartsWith(kind + ".", StringComparison.Ordinal)))
        {
            var target = Path.Combine(folder, className + file[kind.ToString().Length..]); // Window.axaml.cs → Name.axaml.cs
            Write(target, Replace(text, tokens));
            if (target.EndsWith(profile.FileExtension, StringComparison.OrdinalIgnoreCase))
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
