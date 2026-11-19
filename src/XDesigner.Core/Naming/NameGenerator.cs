using System.Xml.Linq;
using XDesigner.Core.Xaml;
using Microsoft.CodeAnalysis.CSharp;

namespace XDesigner.Core.Naming;

/// <summary>
/// WinForms-style control names (§7.4): <c>button1</c>, <c>textBox2</c>. Avalonia turns every <c>x:Name</c>
/// into a field in the code-behind, so these are what the user writes in C# (<c>button1.Content = "Hi";</c>).
/// </summary>
public static class NameGenerator
{
    private static readonly XName XName_ = XName.Get("Name", XamlNamespaces.Xaml);

    /// <summary>The first unused <c>{camelCaseType}{n}</c> in the document, n ≥ 1.</summary>
    public static string Next(XamlDocument document, string typeName, IEnumerable<string>? alsoTaken = null)
    {
        var taken = UsedNames(document);
        if (alsoTaken is not null)
        {
            taken.UnionWith(alsoTaken);
        }

        var stem = CamelCase(typeName);
        for (var n = 1; ; n++)
        {
            var candidate = stem + n;
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>All <c>x:Name</c>/<c>Name</c> values in the document.</summary>
    public static HashSet<string> UsedNames(XamlDocument document) =>
        document.Xml.Root!.DescendantsAndSelf()
            .Select(e => (string?)e.Attribute(XName_) ?? (string?)e.Attribute("Name"))
            .Where(n => n is not null)
            .ToHashSet(StringComparer.Ordinal)!;

    /// <summary>Whether <paramref name="name"/> can be a control name: a C# identifier, not a keyword, not used by another element.</summary>
    public static bool IsValid(XamlDocument document, string name, out string? problem, int? exceptNodeId = null)
    {
        if (!SyntaxFacts.IsValidIdentifier(name))
        {
            problem = "Names must start with a letter or _ and contain only letters, digits and _.";
            return false;
        }

        if (SyntaxFacts.GetKeywordKind(name) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None)
        {
            problem = $"'{name}' is a C# keyword.";
            return false;
        }

        var clash = document.AllNodes.FirstOrDefault(n => n.Name == name && n.Id != exceptNodeId);
        if (clash is not null)
        {
            problem = $"Another control is already named '{name}'.";
            return false;
        }

        problem = null;
        return true;
    }

    /// <summary><c>TextBox</c> → <c>textBox</c>, <c>UIElement</c> → <c>uiElement</c>.</summary>
    public static string CamelCase(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return "control";
        }

        var upper = 0;
        while (upper < typeName.Length && char.IsUpper(typeName[upper]))
        {
            upper++;
        }

        // Keep the last capital of an acronym run when a lowercase letter follows ("UIElement" → "uiElement").
        var lowerCount = upper > 1 && upper < typeName.Length ? upper - 1 : Math.Max(upper, 1);
        return typeName[..lowerCount].ToLowerInvariant() + typeName[lowerCount..];
    }
}
