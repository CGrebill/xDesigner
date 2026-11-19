using System.Collections.Concurrent;
using System.Reflection;
using Avalonia.Metadata;

namespace XDesigner.RenderHost.Rendering;

/// <summary>
/// Resolves XAML element names (XML namespace + local name) to CLR types the way Avalonia does:
/// <c>using:</c> and <c>clr-namespace:</c> namespaces directly, URL namespaces through
/// <see cref="XmlnsDefinitionAttribute"/> on the loaded assemblies.
/// </summary>
internal sealed class XamlTypeResolver
{
    private readonly ConcurrentDictionary<(string Ns, string Name), Type?> _cache = new();
    private Dictionary<string, List<(Assembly Assembly, string ClrNamespace)>> _xmlnsMap = new();
    private int _assemblyCountAtMapBuild = -1;

    public Type? Resolve(string xmlNamespace, string localName)
    {
        RefreshMapIfAssembliesChanged();
        return _cache.GetOrAdd((xmlNamespace, localName), key => ResolveCore(key.Ns, key.Name));
    }

    private Type? ResolveCore(string xmlNamespace, string localName)
    {
        if (xmlNamespace.StartsWith("using:", StringComparison.Ordinal))
        {
            return FindInAllAssemblies(xmlNamespace["using:".Length..], localName);
        }

        if (xmlNamespace.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            var parts = xmlNamespace["clr-namespace:".Length..].Split(';');
            var clrNamespace = parts[0];
            var assemblyName = parts.Skip(1).FirstOrDefault(p => p.StartsWith("assembly=", StringComparison.Ordinal))?["assembly=".Length..];
            if (assemblyName is null)
            {
                return FindInAllAssemblies(clrNamespace, localName);
            }

            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assemblyName);
            return assembly?.GetType($"{clrNamespace}.{localName}", throwOnError: false);
        }

        if (_xmlnsMap.TryGetValue(xmlNamespace, out var targets))
        {
            foreach (var (assembly, clrNamespace) in targets)
            {
                if (assembly.GetType($"{clrNamespace}.{localName}", throwOnError: false) is { } type)
                {
                    return type;
                }
            }
        }

        return null;
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

    private void RefreshMapIfAssembliesChanged()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        if (assemblies.Length == _assemblyCountAtMapBuild)
        {
            return;
        }

        var map = new Dictionary<string, List<(Assembly, string)>>();
        foreach (var assembly in assemblies.Where(a => !a.IsDynamic))
        {
            IEnumerable<XmlnsDefinitionAttribute> definitions;
            try
            {
                definitions = assembly.GetCustomAttributes<XmlnsDefinitionAttribute>();
            }
            catch
            {
                continue;
            }

            foreach (var definition in definitions)
            {
                if (!map.TryGetValue(definition.XmlNamespace, out var list))
                {
                    map[definition.XmlNamespace] = list = [];
                }

                list.Add((assembly, definition.ClrNamespace));
            }
        }

        _xmlnsMap = map;
        _assemblyCountAtMapBuild = assemblies.Length;
        _cache.Clear(); // a newly loaded assembly may resolve names that failed before
    }
}
