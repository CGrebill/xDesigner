using System.Security.Cryptography;
using XDesigner.Core.Frameworks;

namespace XDesigner.Core.Assets;

/// <summary>An image made available to the project: its XAML path (<c>/Assets/logo.png</c>, <c>ms-appx:///Assets/logo.png</c>, <c>logo.png</c>) and file.</summary>
/// <param name="Copied">False when the image was already in the project (same name and content), so nothing changed on disk.</param>
public sealed record AssetImport(string XamlPath, string FilePath, bool Copied);

/// <summary>
/// The project's images (§12): listed from the framework's images folder (<see cref="FrameworkProfile.AssetsFolder"/>),
/// imported by copying into it (an identical file is reused, a different file with the same name gets a numbered name),
/// and addressed in XAML as the framework writes project files: <c>/Assets/{file}</c> in Avalonia (an AvaloniaResource
/// packaged into the assembly), <c>ms-appx:///Assets/{file}</c> in WinUI (a Content file next to the app), and the bare
/// file name in MAUI (a MauiImage in <c>Resources\Images</c>, whose names must be lowercase letters, digits and <c>_</c>).
/// </summary>
public static class ProjectAssets
{
    public const string FolderName = "Assets";

    /// <summary>File types the image picker offers (SVG needs an extra package in Avalonia, so it isn't one of them).</summary>
    public static readonly IReadOnlyList<string> ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".webp"];

    /// <summary>The image types of a framework: MAUI also takes SVG, which its build turns into PNG (review L9).</summary>
    public static IReadOnlyList<string> ImageExtensionsFor(FrameworkProfile? profile) => IsMaui(profile) ? [.. ImageExtensions, ".svg"] : ImageExtensions;

    public static bool IsImage(string path, FrameworkProfile? profile = null) => ImageExtensionsFor(profile).Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>XAML paths of the project's images, sorted.</summary>
    public static IReadOnlyList<string> ListImages(string projectDirectory, FrameworkProfile? profile = null)
    {
        var folder = Path.Combine(projectDirectory, FolderOf(profile));
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(folder, "*", IsMaui(profile) ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories)
            .Where(f => IsImage(f, profile))
            .Select(f => ToXamlPath(projectDirectory, f, profile))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// <c>/Assets/icons/save.png</c> for a file inside the project; <c>ms-appx:///Assets/icons/save.png</c> in WinUI;
    /// <c>save.png</c> in MAUI.
    /// </summary>
    public static string ToXamlPath(string projectDirectory, string filePath, FrameworkProfile? profile = null)
    {
        if (IsMaui(profile))
        {
            // An SVG MauiImage is used as the PNG MAUI makes of it: dotnet_bot.svg is Source="dotnet_bot.png".
            return Path.GetExtension(filePath).Equals(".svg", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(filePath) + ".png"
                : Path.GetFileName(filePath);
        }

        return (profile?.ResourceUriScheme == "ms-appx" ? "ms-appx:///" : "/") + Path.GetRelativePath(projectDirectory, filePath).Replace('\\', '/');
    }

    /// <summary>
    /// The file a project-relative XAML path (<c>/Assets/x.png</c>, <c>ms-appx:///Assets/x.png</c>; in MAUI a bare <c>x.png</c>)
    /// points to, or null for anything else.
    /// </summary>
    public static string? ToFilePath(string projectDirectory, string? xamlPath, FrameworkProfile? profile = null)
    {
        if (IsMaui(profile) && xamlPath is { Length: > 0 } && !xamlPath.Contains('/') && !xamlPath.Contains('\\') && xamlPath != ".." && !xamlPath.Contains(':'))
        {
            var file = Path.Combine(Path.GetFullPath(projectDirectory), FolderOf(profile), xamlPath);
            var svg = Path.ChangeExtension(file, ".svg");
            return !File.Exists(file) && Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(svg) ? svg : file;
        }

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

    /// <summary>Copies <paramref name="sourcePath"/> into the images folder unless it's there already.</summary>
    public static AssetImport Import(string projectDirectory, string sourcePath, FrameworkProfile? profile = null)
    {
        var source = Path.GetFullPath(sourcePath);
        var folder = Path.Combine(Path.GetFullPath(projectDirectory), FolderOf(profile));
        if (source.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return new AssetImport(ToXamlPath(projectDirectory, source, profile), source, Copied: false);
        }

        Directory.CreateDirectory(folder);
        var stem = Path.GetFileNameWithoutExtension(source);
        if (IsMaui(profile))
        {
            stem = MauiImageName(stem);
        }

        var extension = Path.GetExtension(source).ToLowerInvariant();
        var hash = Hash(source);
        for (var n = 1; ; n++)
        {
            var target = Path.Combine(folder, n == 1 ? stem + extension : $"{stem}{(IsMaui(profile) ? "_" : "-")}{n}{extension}");
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

    /// <summary>A name MAUI accepts for an image: lowercase letters, digits and <c>_</c>, starting with a letter (<c>My Logo-2</c> → <c>my_logo_2</c>).</summary>
    internal static string MauiImageName(string stem)
    {
        var name = new string(stem.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        return name.Length == 0 || !char.IsAsciiLetter(name[0]) ? "image_" + name : name;
    }

    private static bool IsMaui(FrameworkProfile? profile) => profile?.Family == FrameworkFamily.Maui;

    private static string FolderOf(FrameworkProfile? profile) => profile?.AssetsFolder ?? FolderName;

    private static byte[] Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }
}
