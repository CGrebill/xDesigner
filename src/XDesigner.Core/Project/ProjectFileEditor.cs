using System.Text;
using System.Xml.Linq;

namespace XDesigner.Core.Project;

/// <summary>Small edits to a project file that keep everything else in it as written (§12).</summary>
public static class ProjectFileEditor
{
    public const string AssetsInclude = @"Assets\**";

    /// <summary>
    /// Makes sure the project packages <c>Assets/</c> as Avalonia resources, adding
    /// <c>&lt;AvaloniaResource Include="Assets\**" /&gt;</c> in its own item group when nothing covers it yet.
    /// Returns true when the file was changed.
    /// </summary>
    public static bool EnsureAvaloniaResource(string projectPath)
    {
        var bytes = File.ReadAllBytes(projectPath);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        if (CoversAssets(XDocument.Parse(text)))
        {
            return false;
        }

        var end = text.LastIndexOf("</Project>", StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(projectPath)} has no </Project> end tag.");
        }

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var indent = IndentOf(text);
        var lineStart = text.LastIndexOf('\n', Math.Max(0, end - 1)) + 1;
        var before = text[..lineStart].TrimEnd(' ', '\t');
        var group = $"{indent}<ItemGroup>{newline}{indent}{indent}<AvaloniaResource Include=\"{AssetsInclude}\" />{newline}{indent}</ItemGroup>{newline}";
        var separator = before.EndsWith(newline + newline, StringComparison.Ordinal) ? "" : newline;
        var updated = before + separator + group + newline + text[end..];

        var output = new UTF8Encoding(hasBom).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(updated)).ToArray();
        var temp = projectPath + ".tmp";
        File.WriteAllBytes(temp, output);
        File.Move(temp, projectPath, overwrite: true);
        return true;
    }

    /// <summary>True when an AvaloniaResource item includes the Assets folder (<c>Assets\**</c>, <c>Assets/**/*</c>, …).</summary>
    public static bool CoversAssets(XDocument project) =>
        project.Descendants().Where(e => e.Name.LocalName == "AvaloniaResource").Any(e =>
            ((string?)e.Attribute("Include") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(part => part.Replace('/', '\\').StartsWith(@"Assets\**", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The indentation of the project's first child element (two spaces when it can't tell).</summary>
    private static string IndentOf(string text)
    {
        foreach (var line in text.Split('\n').Skip(1))
        {
            var trimmed = line.TrimStart(' ', '\t');
            if (trimmed.StartsWith('<'))
            {
                var indent = line[..(line.Length - trimmed.Length)];
                return indent.Length > 0 ? indent : "  ";
            }
        }

        return "  ";
    }
}
