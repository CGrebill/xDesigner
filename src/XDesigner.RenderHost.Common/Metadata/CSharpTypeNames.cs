namespace XDesigner.RenderHost.Metadata;

/// <summary>Writes CLR types the way C# source spells them, for generated event handlers (§9.3); the same for every host.</summary>
public static class CSharpTypeNames
{
    private static readonly Dictionary<Type, string> Aliases = new()
    {
        [typeof(object)] = "object", [typeof(string)] = "string", [typeof(bool)] = "bool", [typeof(int)] = "int",
        [typeof(long)] = "long", [typeof(double)] = "double", [typeof(float)] = "float", [typeof(decimal)] = "decimal",
        [typeof(short)] = "short", [typeof(byte)] = "byte", [typeof(char)] = "char", [typeof(uint)] = "uint",
        [typeof(ulong)] = "ulong", [typeof(ushort)] = "ushort", [typeof(sbyte)] = "sbyte",
    };

    /// <summary>C# spelling of a type: aliases, <c>T?</c>, generics with qualified arguments, nested types with dots.</summary>
    /// <param name="qualify">Prefix the namespace (the type's own; generic arguments are always qualified).</param>
    public static string Of(Type type, bool qualify)
    {
        if (Aliases.TryGetValue(type, out var alias))
        {
            return alias;
        }

        if (Nullable.GetUnderlyingType(type) is { } inner)
        {
            return Of(inner, qualify) + "?";
        }

        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.IsArray && type.GetElementType() is { } element)
        {
            return Of(element, qualify) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        }

        return Named(type.IsGenericType ? type.GetGenericTypeDefinition() : type, type.IsGenericType ? type.GetGenericArguments() : [], qualify);
    }

    /// <summary>Simple names of a type's base classes, nearest first, without <c>object</c> (an event args type's bases).</summary>
    public static IEnumerable<string> BaseTypeNames(Type type)
    {
        for (var t = type.BaseType; t is not null && t != typeof(object); t = t.BaseType)
        {
            yield return StripArity(t.Name);
        }
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
            name += "<" + string.Join(", ", own.Select(a => Of(a, qualify: true))) + ">";
        }

        if (outer is not null)
        {
            return Named(outer, arguments.Take(outerCount).ToArray(), qualify) + "." + name;
        }

        return qualify && !string.IsNullOrEmpty(definition.Namespace) ? $"{definition.Namespace}.{name}" : name;
    }

    private static string StripArity(string name) => name.IndexOf('`') is var i and >= 0 ? name[..i] : name;
}
