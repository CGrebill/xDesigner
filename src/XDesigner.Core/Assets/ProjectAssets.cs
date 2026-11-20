using System.Security.Cryptography;
using XDesigner.Core.Frameworks;

namespace XDesigner.Core.Assets;

/// <summary>An image made available to the project: its XAML path (<c>/Assets/logo.png</c>, <c>ms-appx:///Assets/logo.png</c>) and file.</summary>
/// <param name="Copied">False when the image was already in the project (same name and content), so nothing changed on disk.</param>
public sealed record AssetImport(string XamlPath, string FilePath, bool Copied);

/// <summary>
/// The project's images (§12): listed from <c>Assets/</c>, imported by copying into it (an identical file is reused,
/// a different file with the same name gets a numbered name), and addressed in XAML as the framework writes project
/// files: <c>/Assets/{file}</c> in Avalonia (an AvaloniaResource packaged into the assembly), <c>ms-appx:///Assets/{file}</c>
/// in WinUI (a Content file next to the app).
/// </summary>
public static class ProjectAssets
{
    public const string FolderName = "Assets";

    /// <summary>File types the image picker offers (SVG needs an extra package, so it isn't one of them).</summary>
    public static readonly IReadOnlyList<string> ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".webp"];

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>XAML paths of the images under <c>Assets/</c>, sorted.</summary>
    public static IReadOnlyList<string> ListImages(string projectDirectory, FrameworkProfile? profile = null)
    {
        var folder = Path.Combine(projectDirectory, FolderName);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(IsImage)
            .Select(f => ToXamlPath(projectDirectory, f, profile))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary><c>/Assets/icons/save.png</c> (<c>ms-appx:///Assets/icons/save.png</c> in WinUI) for a file inside the project.</summary>
    public static string ToXamlPath(string projectDirectory, string filePath, FrameworkProfile? profile = null) =>
        (profile?.ResourceUriScheme == "ms-appx" ? "ms-appx:///" : "/") + Path.GetRelativePath(projectDirectory, filePath).Replace('\\', '/');

    /// <summary>The file a project-relative XAML path (<c>/Assets/x.png</c>, <c>ms-appx:///Assets/x.png</c>) points to, or null for anything else.</summary>
    public static string? ToFilePath(string projectDirectory, string? xamlPath)
    {
        if (xamlPath?.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase) == true)
        {
            xamlPath = xamlPath["ms-appx://".Length..];
        }

        if (string.IsNullOrWhiteSpace(xamlPath) || !xamlPath.StartsWith('/') || xamlPath.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, xamlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null; // never outside the project folder
    }

    /// <summary>Copies <paramref name="sourcePath"/> into <c>Assets/</c> unless it's there already.</summary>
    public static AssetImport Import(string projectDirectory, string sourcePath, FrameworkProfile? profile = null)
    {
        var source = Path.GetFullPath(sourcePath);
        var folder = Path.Combine(Path.GetFullPath(projectDirectory), FolderName);
        if (source.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return new AssetImport(ToXamlPath(projectDirectory, source, profile), source, Copied: false);
        }

        Directory.CreateDirectory(folder);
        var stem = Path.GetFileNameWithoutExtension(source);
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var hash = Hash(source);
        for (var n = 1; ; n++)
        {
            var target = Path.Combine(folder, n == 1 ? stem + extension : $"{stem}-{n}{extension}");
            if (!File.Exists(target))
            {
                File.Copy(source, target);
                return new AssetImport(ToXamlPath(projectDirectory, target, profile), target, Copied: true);
            }

            if (Hash(target).SequenceEqual(hash))
            {
                return new AssetImport(ToXamlPath(projectDirectory, target, profile), target, Copied: false);
            }
        }
    }

    private static byte[] Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }
}
