using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace XDesigner.Core.Frameworks;

/// <summary>What a project was found to be built with.</summary>
/// <param name="Family">The framework family; null when the project uses none the designer knows.</param>
/// <param name="Version">The framework's version, when it could be read (from a build, or a package reference).</param>
/// <param name="Profile">The matching profile; null for a version the designer has no profile for (Avalonia 10, say).</param>
public sealed record DetectedFramework(FrameworkFamily? Family, Version? Version, FrameworkProfile? Profile)
{
    public static readonly DetectedFramework Unknown = new(null, null, null);

    /// <summary>Why the designer can't design this project, or null when it can.</summary>
    public string? Problem(string projectName) => this switch
    {
        { Family: null } => $"{projectName} doesn't use a UI framework the designer knows (Avalonia, WinUI 3 or .NET MAUI).",
        { Profile: null } => $"{projectName} uses {FamilyName(Family.Value)}{(Version is { } v ? $" {v.Major}.{v.Minor}" : "")}. The designer supports {Supported()}.",
        { Profile.IsSupported: false } => $"{projectName} is a {Profile.DisplayName} project. Designing {Profile.DisplayName} is coming in a later version of xDesigner.",
        _ => null,
    };

    /// <summary>The supported versions, by framework: <c>Avalonia 11 and 12, WinUI 3 and .NET MAUI 10 and 11</c>.</summary>
    internal static string Supported()
    {
        var groups = FrameworkProfile.All.Where(p => p.IsSupported)
            .GroupBy(p => p.Family)
            .Select(g => $"{FamilyName(g.Key)} {string.Join(" and ", g.Select(p => p.MajorVersion).Order())}")
            .ToList();
        return groups.Count <= 1 ? string.Concat(groups) : $"{string.Join(", ", groups.SkipLast(1))} and {groups[^1]}";
    }

    private static string FamilyName(FrameworkFamily family) => family switch
    {
        FrameworkFamily.Maui => ".NET MAUI",
        _ => family.ToString(),
    };
}

/// <summary>
/// Finds out which framework, at which major version, a project uses (§11.1): from its build output when it has been built
/// (the reliable answer: the assemblies it actually loads), else from its project file.
/// </summary>
public static class FrameworkDetector
{
    /// <summary>From the build output next to <paramref name="targetPath"/>; <see cref="DetectedFramework.Unknown"/> when it can't tell.</summary>
    public static DetectedFramework FromBuildOutput(string targetPath)
    {
        // MAUI first: a MAUI app on Windows also carries the Windows App SDK.
        if (PackageVersion(targetPath, "Microsoft.Maui.Controls") is { } maui)
        {
            return Detected(FrameworkFamily.Maui, maui);
        }

        if (AvaloniaVersion(targetPath) is { } avalonia)
        {
            return Detected(FrameworkFamily.Avalonia, avalonia);
        }

        if (PackageVersion(targetPath, "Microsoft.WindowsAppSDK", "Microsoft.WindowsAppSDK.WinUI") is { } winAppSdk)
        {
            return new DetectedFramework(FrameworkFamily.WinUI, winAppSdk, FrameworkProfile.WinUI3);
        }

        return DetectedFramework.Unknown;
    }

    /// <summary>
    /// From the project file alone (before a build): <c>UseMaui</c>, <c>UseWinUI</c>, or an Avalonia package reference
    /// (its version from the project or a <c>Directory.Packages.props</c> above it).
    /// </summary>
    public static DetectedFramework FromProjectFile(string projectPath)
    {
        XDocument project;
        try
        {
            project = XDocument.Load(projectPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return DetectedFramework.Unknown;
        }

        if (IsTrue(project, "UseMaui"))
        {
            // MAUI's version follows .NET's: net10.0-… is MAUI 10.
            var tfm = Property(project, "TargetFrameworks") ?? Property(project, "TargetFramework") ?? "";
            var major = tfm.Split(';').Select(TfmMajor).Where(m => m > 0).DefaultIfEmpty(0).Max();
            return major > 0 ? Detected(FrameworkFamily.Maui, new Version(major, 0)) : new DetectedFramework(FrameworkFamily.Maui, null, null);
        }

        if (IsTrue(project, "UseWinUI"))
        {
            return new DetectedFramework(FrameworkFamily.WinUI, null, FrameworkProfile.WinUI3);
        }

        var reference = project.Descendants().FirstOrDefault(e => e.Name.LocalName == "PackageReference"
            && ((string?)e.Attribute("Include"))?.Equals("Avalonia", StringComparison.OrdinalIgnoreCase) == true);
        if (reference is null)
        {
            return DetectedFramework.Unknown;
        }

        var text = (string?)reference.Attribute("Version") ?? (string?)reference.Attribute("VersionOverride")
            ?? reference.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value
            ?? CentralVersion(projectPath, "Avalonia");
        return ParseVersion(ResolveProperty(text, project)) is { } version
            ? Detected(FrameworkFamily.Avalonia, version)
            : new DetectedFramework(FrameworkFamily.Avalonia, null, null);
    }

    /// <summary>The build output's answer when there is one, else the project file's.</summary>
    public static DetectedFramework Detect(string projectPath, string? targetPath)
    {
        if (targetPath is { Length: > 0 } && File.Exists(targetPath) && FromBuildOutput(targetPath) is { Family: not null } built)
        {
            return built;
        }

        return FromProjectFile(projectPath);
    }

    private static DetectedFramework Detected(FrameworkFamily family, Version version) =>
        new(family, version, FrameworkProfile.For(family, version.Major));

    /// <summary>
    /// The Avalonia version a built project uses: <c>Avalonia.Base.dll</c> in the output folder (apps), else the
    /// <c>Avalonia</c> package listed in its <c>deps.json</c> (libraries). Null when it can't tell.
    /// </summary>
    public static Version? AvaloniaVersion(string targetPath)
    {
        var dir = Path.GetDirectoryName(targetPath);
        if (dir is null)
        {
            return null;
        }

        var baseDll = Path.Combine(dir, "Avalonia.Base.dll");
        if (File.Exists(baseDll))
        {
            try
            {
                return AssemblyName.GetAssemblyName(baseDll).Version;
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException)
            {
                // Fall back to deps.json.
            }
        }

        return PackageVersion(targetPath, "Avalonia", "Avalonia.Base");
    }

    /// <summary>The version of the first of <paramref name="packageIds"/> listed in the build's <c>deps.json</c>.</summary>
    public static Version? PackageVersion(string targetPath, params string[] packageIds)
    {
        var deps = Path.ChangeExtension(targetPath, ".deps.json");
        if (!File.Exists(deps))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(deps));
            if (!json.RootElement.TryGetProperty("libraries", out var libraries))
            {
                return null;
            }

            foreach (var library in libraries.EnumerateObject())
            {
                var parts = library.Name.Split('/');
                if (parts.Length == 2 && packageIds.Contains(parts[0], StringComparer.OrdinalIgnoreCase) && ParseVersion(parts[1]) is { } version)
                {
                    return version;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Unreadable deps.json.
        }

        return null;
    }

    private static Version? ParseVersion(string? text) =>
        text is null ? null : Version.TryParse(text.Trim().TrimStart('[').Split(',', '-', ']')[0].Replace("*", "0", StringComparison.Ordinal), out var v) ? v : null;

    /// <summary><c>net10.0-windows10.0.19041.0</c> → 10.</summary>
    private static int TfmMajor(string tfm) =>
        tfm.Trim() is { } t && t.StartsWith("net", StringComparison.OrdinalIgnoreCase) && int.TryParse(t[3..].Split('.', '-')[0], out var major) ? major : 0;

    private static string? Property(XDocument project, string name) =>
        project.Descendants().FirstOrDefault(e => e.Name.LocalName == name && e.Parent?.Name.LocalName == "PropertyGroup")?.Value.Trim();

    private static bool IsTrue(XDocument project, string name) =>
        string.Equals(Property(project, name), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>$(AvaloniaVersion)</c> → the property's value in the project, when it is defined there.</summary>
    private static string? ResolveProperty(string? text, XDocument project)
    {
        if (text is not null && text.StartsWith("$(", StringComparison.Ordinal) && text.EndsWith(')'))
        {
            return Property(project, text[2..^1]);
        }

        return text;
    }

    /// <summary>The version a <c>Directory.Packages.props</c> above the project gives a package (central package management).</summary>
    private static string? CentralVersion(string projectPath, string packageId)
    {
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath)); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var props = Path.Combine(dir, "Directory.Packages.props");
            if (!File.Exists(props))
            {
                continue;
            }

            try
            {
                var doc = XDocument.Load(props);
                var entry = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "PackageVersion"
                    && ((string?)e.Attribute("Include"))?.Equals(packageId, StringComparison.OrdinalIgnoreCase) == true);
                return ResolveProperty((string?)entry?.Attribute("Version"), doc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return null;
            }
        }

        return null;
    }
}
