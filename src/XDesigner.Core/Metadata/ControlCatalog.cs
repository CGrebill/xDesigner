using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;

namespace XDesigner.Core.Metadata;

/// <summary>
/// The control types reported by the render host, looked up the way XAML names them:
/// <c>(https://github.com/avaloniaui, Button)</c>, <c>(using:My.Controls, StarRating)</c> or
/// <c>(clr-namespace:My.Controls;assembly=My, StarRating)</c>.
/// </summary>
public sealed class ControlCatalog
{
    private readonly Dictionary<string, ControlTypeInfo> _byFullName;
    private readonly Dictionary<(string Ns, string Name), ControlTypeInfo> _byXaml = [];

    public ControlCatalog(IEnumerable<ControlTypeInfo> types)
    {
        All = types.ToList();
        _byFullName = All.GroupBy(t => t.FullName).ToDictionary(g => g.Key, g => g.First());

        // Several CLR namespaces map to the Avalonia XML namespace; prefer creatable types, then Avalonia.Controls itself.
        foreach (var type in All.OrderByDescending(t => t.IsCreatable).ThenByDescending(t => t.ClrNamespace == "Avalonia.Controls"))
        {
            _byXaml.TryAdd((type.XmlNamespace, type.Name), type);
            _byXaml.TryAdd(("using:" + type.ClrNamespace, type.Name), type);
        }
    }

    public static ControlCatalog Empty { get; } = new([]);

    public IReadOnlyList<ControlTypeInfo> All { get; }

    public bool IsEmpty => All.Count == 0;

    public ControlTypeInfo? Find(string fullName) => _byFullName.GetValueOrDefault(fullName);

    /// <summary>Resolves an element name as written in XAML.</summary>
    public ControlTypeInfo? Resolve(string xmlNamespace, string localName)
    {
        if (_byXaml.TryGetValue((xmlNamespace, localName), out var type))
        {
            return type;
        }

        return ClrNamespaceOf(xmlNamespace) is { } clr ? _byXaml.GetValueOrDefault(("using:" + clr, localName)) : null;
    }

    public ControlTypeInfo? Resolve(ElementNode node) => Resolve(node.XmlNamespace, node.TypeName);

    /// <summary>The CLR namespace a <c>using:</c> or <c>clr-namespace:</c> XML namespace refers to; null for URL namespaces.</summary>
    public static string? ClrNamespaceOf(string xmlNamespace)
    {
        if (xmlNamespace.StartsWith("using:", StringComparison.Ordinal))
        {
            return xmlNamespace["using:".Length..];
        }

        if (xmlNamespace.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            return xmlNamespace["clr-namespace:".Length..].Split(';')[0];
        }

        return null;
    }
}
