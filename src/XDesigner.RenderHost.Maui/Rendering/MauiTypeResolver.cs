using System.Collections.Concurrent;
using System.Reflection;

namespace XDesigner.RenderHost.Rendering;

/// <summary>
/// Resolves XAML element names (XML namespace + local name) to CLR types the way MAUI does: <c>clr-namespace:…;assembly=…</c>
/// (and <c>using:</c>) directly, URL namespaces through the <c>XmlnsDefinition</c> attributes of the loaded assemblies.
/// </summary>
internal sealed class MauiTypeResolver
{
    public const string MauiNamespace = "http://schemas.microsoft.com/dotnet/2021/maui";

    private readonly ConcurrentDictionary<(string Ns, string Name), Type?> _cache = new();
    private Dictionary<string, List<(Assembly Assembly, string ClrNamespace)>> _xmlnsMap = [];
    private int _assemblyCountAtMapBuild = -1;

    public Type? Resolve(string xmlNamespace, string localName)
    {
        RefreshMapIfAssembliesChanged();
        return _cache.GetOrAdd((xmlNamespace, localName), key => ResolveCore(key.Ns, key.Name));
    }

    /// <summary>The XML namespaces mapped to a CLR namespace of an assembly (for writing a type's namespace).</summary>
    public bool IsMappedTo(string xmlNamespace, Type type)
    {
        RefreshMapIfAssembliesChanged();
        return _xmlnsMap.TryGetValue(xmlNamespace, out var targets) && targets.Any(t => t.Assembly == type.Assembly && t.ClrNamespace == type.Namespace);
    }

    /// <summary>MAUI's global namespace (MAUI 10+): an aggregate of other XML namespaces, MAUI's own among them.</summary>
    public const string GlobalNamespace = "http://schemas.microsoft.com/dotnet/maui/global";

    /// <summary>
    /// The names MAUI's XAML loader tries for an element: as written, with the <c>Extension</c> suffix (<c>StaticResource</c>
    /// is <c>StaticResourceExtension</c>), and generic (<c>OnPlatform x:TypeArguments="…"</c> is <c>OnPlatform`1</c>).
    /// </summary>
    private Type? ResolveCore(string xmlNamespace, string localName) =>
        ResolveExact(xmlNamespace, localName)
        ?? ResolveExact(xmlNamespace, localName + "Extension")
        ?? ResolveExact(xmlNamespace, localName + "`1")
        ?? ResolveExact(xmlNamespace, localName + "`2");

    private Type? ResolveExact(string xmlNamespace, string localName)
    {
        if (xmlNamespace.StartsWith("using:", StringComparison.Ordinal))
        {
            return FindInAllAssemblies(xmlNamespace["using:".Length..], localName);
        }

        if (xmlNamespace.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            var parts = xmlNamespace["clr-namespace:".Length..].Split(';');
            var assemblyName = parts.Skip(1).FirstOrDefault(p => p.StartsWith("assembly=", StringComparison.Ordinal))?["assembly=".Length..];
            if (assemblyName is null)
            {
                return FindInAllAssemblies(parts[0], localName);
            }

            return LoadedOrLoad(assemblyName)?.GetType($"{parts[0]}.{localName}", throwOnError: false);
        }

        if (!_xmlnsMap.ContainsKey(xmlNamespace) && xmlNamespace == GlobalNamespace)
        {
            xmlNamespace = MauiNamespace; // no project registers more: the global namespace is MAUI's own
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

    /// <summary>
    /// The loaded assembly a CLR namespace is in (the user's first), for <c>clr-namespace:</c> declarations without an
    /// assembly: MAUI's compiler takes them as the project's, its runtime loader as the root type's (MAUI's own).
    /// </summary>
    public static string? AssemblyOfNamespace(string clrNamespace, System.Reflection.Assembly? preferred)
    {
        static bool Has(System.Reflection.Assembly assembly, string ns)
        {
            try
            {
                return assembly.GetTypes().Any(t => t.Namespace == ns);
            }
            catch (System.Reflection.ReflectionTypeLoadException ex)
            {
                return ex.Types.Any(t => t?.Namespace == ns);
            }
        }

        if (preferred is not null && Has(preferred, clrNamespace))
        {
            return preferred.GetName().Name;
        }

        return AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).FirstOrDefault(a => Has(a, clrNamespace))?.GetName().Name;
    }

    /// <summary>
    /// The loaded assembly of that name, else loaded now from the app folder (the shadow copy of the project's output): a
    /// library nothing has used yet is loaded only when the XAML loader first meets it, after the sanitizer has run.
    /// </summary>
    private static Assembly? LoadedOrLoad(string assemblyName)
    {
        if (AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assemblyName) is { } loaded)
        {
            return loaded;
        }

        try
        {
            return Assembly.Load(new AssemblyName(assemblyName));
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or ArgumentException)
        {
            return null;
        }
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
            IEnumerable<CustomAttributeData> definitions;
            try
            {
                definitions = assembly.GetCustomAttributesData().Where(a => a.AttributeType.Name == "XmlnsDefinitionAttribute").ToList();
            }
            catch
            {
                continue;
            }

            foreach (var definition in definitions)
            {
                if (definition.ConstructorArguments is [{ Value: string xmlNamespace }, { Value: string clrNamespace }, ..])
                {
                    var target = definition.NamedArguments.FirstOrDefault(n => n.MemberName == "AssemblyName").TypedValue.Value as string;
                    var targetAssembly = target is null ? assembly : assemblies.FirstOrDefault(a => a.GetName().Name == target);
                    if (targetAssembly is null)
                    {
                        continue;
                    }

                    if (!map.TryGetValue(xmlNamespace, out var list))
                    {
                        map[xmlNamespace] = list = [];
                    }

                    list.Add((targetAssembly, clrNamespace));
                }
            }
        }

        // A definition whose "CLR namespace" is itself an XML namespace aggregates that namespace (MAUI's global one:
        // XmlnsDefinition(".../maui/global", ".../2021/maui")): it resolves to everything that namespace maps to.
        foreach (var (xmlNamespace, list) in map)
        {
            var aggregated = list.Where(t => t.Item2.Contains("://", StringComparison.Ordinal)).Select(t => t.Item2).Distinct().ToList();
            list.RemoveAll(t => t.Item2.Contains("://", StringComparison.Ordinal));
            foreach (var other in aggregated.Where(o => o != xmlNamespace))
            {
                if (map.TryGetValue(other, out var targets))
                {
                    list.AddRange(targets.Where(t => !t.Item2.Contains("://", StringComparison.Ordinal)));
                }
            }
        }

        _xmlnsMap = map;
        _assemblyCountAtMapBuild = assemblies.Length;
        _cache.Clear(); // a newly loaded assembly may resolve names that failed before
    }
}
