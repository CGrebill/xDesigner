using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.UI.Xaml;

namespace XDesigner.RenderHost.Rendering;

/// <summary>
/// Resolves XAML element names (XML namespace + local name) to CLR types the way WinUI does: the presentation namespace
/// to the <c>Microsoft.UI.Xaml.*</c> types, <c>using:</c> namespaces to any loaded assembly.
/// </summary>
internal sealed class XamlTypeResolver
{
    public const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>Where presentation-namespace names are looked up first when two namespaces have the same name.</summary>
    private static readonly string[] PreferredNamespaces =
    [
        "Microsoft.UI.Xaml.Controls", "Microsoft.UI.Xaml.Controls.Primitives", "Microsoft.UI.Xaml", "Microsoft.UI.Xaml.Shapes",
        "Microsoft.UI.Xaml.Media", "Microsoft.UI.Xaml.Documents", "Microsoft.UI.Xaml.Media.Imaging", "Microsoft.UI.Xaml.Media.Animation",
    ];

    private readonly ConcurrentDictionary<(string Ns, string Name), Type?> _cache = new();
    private readonly Lazy<Dictionary<string, Type>> _presentation = new(BuildPresentationMap);

    /// <summary>Found types are kept; a miss is looked up again next time, as the library may have loaded since (review M24).</summary>
    public Type? Resolve(string xmlNamespace, string localName)
    {
        if (_cache.TryGetValue((xmlNamespace, localName), out var cached))
        {
            return cached;
        }

        var type = ResolveCore(xmlNamespace, localName);
        if (type is not null)
        {
            _cache[(xmlNamespace, localName)] = type;
        }

        return type;
    }

    private Type? ResolveCore(string xmlNamespace, string localName)
    {
        if (xmlNamespace == PresentationNamespace)
        {
            return _presentation.Value.GetValueOrDefault(localName);
        }

        return xmlNamespace.StartsWith("using:", StringComparison.Ordinal)
            ? FindInAllAssemblies(xmlNamespace["using:".Length..], localName)
            : null; // WinUI XAML names CLR namespaces with using: only
    }

    private static Type? FindInAllAssemblies(string clrNamespace, string localName)
    {
        var fullName = $"{clrNamespace}.{localName}";
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.IsDynamic && assembly.GetType(fullName, throwOnError: false) is { } type)
            {
                return type;
            }
        }

        return null;
    }

    /// <summary>Every public type of the WinUI projection in a <c>Microsoft.UI.Xaml*</c> namespace, by simple name.</summary>
    private static Dictionary<string, Type> BuildPresentationMap()
    {
        Type?[] types;
        try
        {
            types = typeof(UIElement).Assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }

        var map = new Dictionary<string, Type>(StringComparer.Ordinal);
        var candidates = types
            .OfType<Type>()
            .Where(t => t is { IsPublic: true, IsGenericTypeDefinition: false } && t.Namespace?.StartsWith("Microsoft.UI.Xaml", StringComparison.Ordinal) == true)
            .OrderBy(t => Array.IndexOf(PreferredNamespaces, t.Namespace) is var i and >= 0 ? i : PreferredNamespaces.Length);
        foreach (var type in candidates)
        {
            map.TryAdd(type.Name, type);
        }

        return map;
    }
}
