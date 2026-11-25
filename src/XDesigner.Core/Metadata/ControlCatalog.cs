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

    /// <param name="profile">The framework whose render host reported <paramref name="types"/>; Avalonia 12 when omitted.</param>
    public ControlCatalog(IEnumerable<ControlTypeInfo> types, Frameworks.FrameworkProfile? profile = null)
    {
        All = types.ToList();
        Profile = profile ?? Frameworks.FrameworkProfile.Avalonia12;
        _byFullName = All.GroupBy(t => t.FullName).ToDictionary(g => g.Key, g => g.First());

        // Several CLR namespaces map to the framework's XML namespace; prefer creatable types, then its main controls namespace.
        foreach (var type in All.OrderByDescending(t => t.IsCreatable).ThenByDescending(t => t.ClrNamespace == Profile.ControlsNamespace))
        {
            _byXaml.TryAdd((type.XmlNamespace, type.Name), type);
            _byXaml.TryAdd(("using:" + type.ClrNamespace, type.Name), type);
        }
    }

    public static ControlCatalog Empty { get; } = new([]);

    /// <summary>The framework these types belong to.</summary>
    public Frameworks.FrameworkProfile Profile { get; }

    /// <summary>Whether <paramref name="type"/> is, or derives from, the framework's built-in control <paramref name="name"/>.</summary>
    public bool IsA(ControlTypeInfo? type, string name) => type?.DerivesFrom(Profile.BuiltIn(name)) == true;

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

        if (xmlNamespace == Frameworks.FrameworkProfile.MauiGlobalNamespace)
        {
            // An aggregate: MAUI's own controls, then any type the project registered for it (review M15).
            return Resolve(Frameworks.FrameworkProfile.MauiNamespace, localName)
                ?? All.Where(t => t.Name == localName).OrderByDescending(t => t.IsCreatable).FirstOrDefault();
        }

        if (xmlNamespace == Frameworks.FrameworkProfile.MauiNamespace
            && _byXaml.GetValueOrDefault((Frameworks.FrameworkProfile.MauiGlobalNamespace, localName)) is { } registered)
        {
            return registered; // an unprefixed project control in a file without namespaces (MAUI 11)
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
