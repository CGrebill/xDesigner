using System.Collections.Concurrent;
using System.Reflection;
using Avalonia.Interactivity;
using XDesigner.Protocol.Metadata;

namespace XDesigner.RenderHost.Metadata;

/// <summary>
/// Events a control type can connect to code-behind handlers (§9.3, §14.3): public CLR events whose delegate is
/// <c>void (sender, e)</c>. Signatures come from the delegate's <c>Invoke</c> method, written as C# types.
/// </summary>
internal sealed class EventMetadataProvider
{
    private static readonly Dictionary<Type, string> Aliases = new()
    {
        [typeof(object)] = "object", [typeof(string)] = "string", [typeof(bool)] = "bool", [typeof(int)] = "int",
        [typeof(long)] = "long", [typeof(double)] = "double", [typeof(float)] = "float", [typeof(decimal)] = "decimal",
        [typeof(short)] = "short", [typeof(byte)] = "byte", [typeof(char)] = "char", [typeof(uint)] = "uint",
        [typeof(ulong)] = "ulong", [typeof(ushort)] = "ushort", [typeof(sbyte)] = "sbyte",
    };

    private readonly ConcurrentDictionary<Type, EventInfoDto[]> _cache = new();

    public EventInfoDto[] For(Type type) => _cache.GetOrAdd(type, Describe);

    private static EventInfoDto[] Describe(Type type)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(t.TypeHandle); // routed events register statically
        }

        var routed = new HashSet<string>(StringComparer.Ordinal);
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (var e in RoutedEventRegistry.Instance.GetRegistered(t))
            {
                routed.Add(e.Name);
            }
        }

        var result = new List<EventInfoDto>();
        foreach (var evt in type.GetEvents(BindingFlags.Public | BindingFlags.Instance).GroupBy(e => e.Name).Select(g => g.First()))
        {
            if (evt.EventHandlerType?.GetMethod("Invoke") is not { } invoke || invoke.ReturnType != typeof(void)
                || invoke.GetParameters() is not [var sender, var args])
            {
                continue;
            }

            var argsType = args.ParameterType;
            result.Add(new EventInfoDto(
                evt.Name,
                routed.Contains(evt.Name),
                sender.ParameterType == typeof(object) ? "object?" : CSharpName(sender.ParameterType, qualify: true),
                CSharpName(argsType, qualify: false),
                argsType.Namespace ?? "",
                BaseTypes(argsType).ToArray()));
        }

        return result.OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> BaseTypes(Type type)
    {
        for (var t = type.BaseType; t is not null && t != typeof(object); t = t.BaseType)
        {
            yield return StripArity(t.Name);
        }
    }

    /// <summary>C# spelling of a type: aliases, <c>T?</c>, generics with qualified arguments, nested types with dots.</summary>
    internal static string CSharpName(Type type, bool qualify)
    {
        if (Aliases.TryGetValue(type, out var alias))
        {
            return alias;
        }

        if (Nullable.GetUnderlyingType(type) is { } inner)
        {
            return CSharpName(inner, qualify) + "?";
        }

        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.IsArray && type.GetElementType() is { } element)
        {
            return CSharpName(element, qualify) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        }

        return Named(type.IsGenericType ? type.GetGenericTypeDefinition() : type, type.IsGenericType ? type.GetGenericArguments() : [], qualify);
    }

    /// <summary>
    /// A (possibly nested) type definition with its generic arguments. Nested types of generic types carry the outer
    /// type's arguments first (<c>Dictionary&lt;K, V&gt;.KeyCollection</c> has K and V), so they are handed down explicitly.
    /// </summary>
    private static string Named(Type definition, Type[] arguments, bool qualify)
    {
        var outer = definition.IsNested ? definition.DeclaringType : null;
        var outerCount = outer?.GetGenericArguments().Length ?? 0;
        var own = arguments.Skip(outerCount).ToArray();

        var name = StripArity(definition.Name);
        if (own.Length > 0)
        {
            name += "<" + string.Join(", ", own.Select(a => CSharpName(a, qualify: true))) + ">";
        }

        if (outer is not null)
        {
            return Named(outer, arguments.Take(outerCount).ToArray(), qualify) + "." + name;
        }

        return qualify && !string.IsNullOrEmpty(definition.Namespace) ? $"{definition.Namespace}.{name}" : name;
    }

    private static string StripArity(string name) => name.IndexOf('`') is var i and >= 0 ? name[..i] : name;
}
