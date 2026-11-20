using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace XDesigner.RenderHost.Metadata;

/// <summary>
/// Installed font family names, read from the registry (WinUI has no font list API). The registry names faces
/// (<c>Segoe UI Bold (TrueType)</c>), so style words are stripped to get families; the editor still accepts any typed name.
/// </summary>
internal static partial class FontList
{
    private const string FontsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

    /// <summary>Families every WinUI app has (the system's UI fonts), offered first.</summary>
    private static readonly string[] SystemFamilies = ["Segoe UI Variable", "Segoe UI", "Segoe Fluent Icons", "Segoe MDL2 Assets", "Cascadia Mono"];

    public static string[] Families()
    {
        var installed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(FontsKey);
                foreach (var face in key?.GetValueNames() ?? [])
                {
                    foreach (var part in face.Split(" & ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (FamilyOf(part) is { Length: > 0 } family)
                        {
                            installed.Add(family);
                        }
                    }
                }
            }
            catch
            {
                // No font list is fine: the editor still accepts typed names.
            }
        }

        return SystemFamilies.Where(installed.Contains).Concat(installed.Except(SystemFamilies, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary><c>Segoe UI Semibold Italic (TrueType)</c> → <c>Segoe UI</c>.</summary>
    internal static string FamilyOf(string face)
    {
        var name = FormatSuffix().Replace(face, "").Trim();
        string previous;
        do
        {
            previous = name;
            name = StyleSuffix().Replace(name, "").Trim();
        }
        while (name != previous && name.Length > 0);

        return name.Length > 0 ? name : previous;
    }

    [GeneratedRegex(@"\s*\((TrueType|OpenType|All res|VGA res|[^)]*)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FormatSuffix();

    [GeneratedRegex(@"\s+(Regular|Bold|Italic|Oblique|Light|Semilight|SemiLight|Semibold|SemiBold|Demibold|Medium|Black|Heavy|Thin|ExtraLight|Extralight|UltraLight|ExtraBold|Extrabold|UltraBold|Condensed|SemiCondensed|Narrow|Book|BoldItalic|BoldOblique)$", RegexOptions.IgnoreCase)]
    private static partial Regex StyleSuffix();
}
