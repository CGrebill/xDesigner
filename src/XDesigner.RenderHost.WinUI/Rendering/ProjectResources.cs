using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace XDesigner.RenderHost.Rendering;

/// <summary>
/// What the project's resource index (<c>.pri</c>) resolves in the app, read from the project's files instead (review M27):
/// the host can't use the project's index as its own (WinUI fails to start with it), so the sanitizer fills in
/// <c>x:Uid</c> strings from <c>Strings\{language}\Resources.resw</c>, and points <c>ms-appx:///</c> images that exist only as
/// scale-qualified files (<c>logo.scale-200.png</c>) at the best of them.
/// </summary>
internal sealed partial class ProjectResources(string? projectDirectory, string appDirectory)
{
    private const string AppxPrefix = "ms-appx:///";

    private readonly Lazy<Dictionary<string, string>> _strings = new(() => LoadStrings(projectDirectory));

    /// <summary><c>Uid.Property</c> → text, in the UI language (else English, else the first language the project has).</summary>
    public IReadOnlyDictionary<string, string> Strings => _strings.Value;

    /// <summary>
    /// <paramref name="value"/>, or for an <c>ms-appx:///</c> file that isn't in the app folder but has scale-qualified
    /// variants, the URI of the one closest to 100%.
    /// </summary>
    public string ResolveAppxUri(string value)
    {
        if (!value.StartsWith(AppxPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        try
        {
            var relative = Uri.UnescapeDataString(value[AppxPrefix.Length..]).Replace('/', Path.DirectorySeparatorChar);
            var file = Path.GetFullPath(Path.Combine(appDirectory, relative));
            var directory = Path.GetDirectoryName(file);
            if (File.Exists(file) || directory is null || !Directory.Exists(directory) || !file.StartsWith(Path.GetFullPath(appDirectory), StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            var stem = Path.GetFileNameWithoutExtension(file);
            var extension = Path.GetExtension(file);
            var best = Directory.EnumerateFiles(directory, $"{stem}.*{extension}")
                .Select(f => (File: f, Scale: ScaleOf(Path.GetFileName(f))))
                .Where(c => c.Scale > 0)
                .OrderBy(c => Math.Abs(c.Scale - 100))
                .Select(c => c.File)
                .FirstOrDefault();
            return best is null ? value : AppxPrefix + Path.GetRelativePath(appDirectory, best).Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return value;
        }
    }

    /// <summary>The scale of <c>logo.scale-200.png</c> (200); 100 for other qualifiers (<c>targetsize-24</c>); 0 for none.</summary>
    private static int ScaleOf(string fileName)
    {
        if (ScaleQualifier().Match(fileName) is { Success: true } scale)
        {
            return int.Parse(scale.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        return fileName.Count(c => c == '.') >= 2 ? 100 : 0;
    }

    private static Dictionary<string, string> LoadStrings(string? projectDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var strings = projectDirectory is null ? null : Path.Combine(projectDirectory, "Strings");
        if (strings is null || !Directory.Exists(strings))
        {
            return result;
        }

        try
        {
            var culture = CultureInfo.CurrentUICulture;
            var languages = Directory.GetDirectories(strings).Select(d => (Path: d, Name: Path.GetFileName(d))).ToList();
            var folder = languages.FirstOrDefault(l => l.Name.Equals(culture.Name, StringComparison.OrdinalIgnoreCase)).Path
                ?? languages.FirstOrDefault(l => l.Name.Split('-')[0].Equals(culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase)).Path
                ?? languages.FirstOrDefault(l => l.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)).Path
                ?? languages.FirstOrDefault().Path;
            var resw = folder is null ? null : Path.Combine(folder, "Resources.resw"); // the default map x:Uid looks in
            if (resw is null || !File.Exists(resw))
            {
                return result;
            }

            foreach (var data in XDocument.Load(resw).Root?.Elements("data") ?? [])
            {
                if ((string?)data.Attribute("name") is { } name && data.Element("value")?.Value is { } text)
                {
                    result[name] = text;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            Console.Error.WriteLine($"The project's strings couldn't be read: {ex.Message}");
        }

        return result;
    }

    [GeneratedRegex(@"\.scale-(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ScaleQualifier();
}
