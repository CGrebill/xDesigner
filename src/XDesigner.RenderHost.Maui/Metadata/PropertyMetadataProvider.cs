using System.Collections.Concurrent;
using System.Reflection;
using XDesigner.Protocol.Metadata;
using MauiColor = Microsoft.Maui.Graphics.Color;

namespace XDesigner.RenderHost.Metadata;

/// <summary>One property as the host knows it: the DTO sent to the app plus how to read it from a live element.</summary>
internal sealed record HostProperty(PropertyInfoDto Info, BindableProperty? Bindable, PropertyInfo? Clr);

/// <summary>
/// The properties an element type can set from XAML (§14.3): bindable properties (a settable CLR property with a static
/// <c>{Name}Property</c> field), other settable CLR properties of simple types, and attached properties of the layouts.
/// Cached per type.
/// </summary>
internal sealed class PropertyMetadataProvider
{
    /// <summary>Owners whose attached properties are offered on every view (the app shows only the relevant ones).</summary>
    private static readonly Type[] AttachedOwners = [typeof(Grid), typeof(AbsoluteLayout), typeof(FlexLayout), typeof(ToolTipProperties), typeof(SemanticProperties)];

    /// <summary>LayoutOptions are written by name in XAML.</summary>
    internal static readonly string[] LayoutOptionNames = ["Start", "Center", "End", "Fill"];

    /// <summary>Types XAML parses from text, so a text box is a fine editor.</summary>
    private static readonly HashSet<Type> TextParsed =
    [
        typeof(Point), typeof(Size), typeof(Rect), typeof(TimeSpan), typeof(DateTime), typeof(DateTimeOffset), typeof(Uri),
        typeof(char), typeof(Microsoft.Maui.Graphics.Font),
    ];

    private readonly ConcurrentDictionary<Type, HostProperty[]> _cache = new();

    public HostProperty[] For(Type type) => _cache.GetOrAdd(type, Describe);

    private static HostProperty[] Describe(Type type)
    {
        var result = new Dictionary<string, HostProperty>(StringComparer.Ordinal);
        foreach (var clr in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (result.ContainsKey(clr.Name) || clr.SetMethod is not { IsPublic: true } || clr.GetIndexParameters().Length > 0
                || clr.DeclaringType is null || clr.DeclaringType.Assembly == typeof(object).Assembly)
            {
                continue;
            }

            var kind = KindOf(clr.Name, clr.PropertyType, out var nullable, out var enumValues);
            var bindable = BindablePropertyOf(type, clr.Name);
            if (bindable is null && kind is ValueKind.Unsupported or ValueKind.Object)
            {
                continue; // a plain CLR property XAML text can't set
            }

            result[clr.Name] = new HostProperty(
                new PropertyInfoDto(clr.Name, clr.DeclaringType.Name, TypeName(clr.PropertyType), kind, false, nullable, enumValues),
                bindable,
                bindable is null ? clr : null);
        }

        if (typeof(View).IsAssignableFrom(type))
        {
            foreach (var owner in AttachedOwners)
            {
                foreach (var (name, valueType, bindable) in AttachedProperties(owner, type))
                {
                    var kind = KindOf(name, valueType, out var nullable, out var enumValues);
                    var info = new PropertyInfoDto(name, owner.Name, TypeName(valueType), kind, true, nullable, enumValues);
                    result.TryAdd(info.Key, new HostProperty(info, bindable, null));
                }
            }
        }

        return result.Values.OrderBy(p => p.Info.Key, StringComparer.Ordinal).ToArray();
    }

    private static BindableProperty? BindablePropertyOf(Type type, string name) =>
        type.GetField(name + "Property", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null) as BindableProperty;

    /// <summary>Attached properties of <paramref name="owner"/> settable on <paramref name="target"/>: static <c>SetX(target, value)</c> with an <c>XProperty</c>.</summary>
    private static IEnumerable<(string Name, Type ValueType, BindableProperty Bindable)> AttachedProperties(Type owner, Type target)
    {
        foreach (var setter in owner.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (setter.Name.StartsWith("Set", StringComparison.Ordinal) && setter.GetParameters() is [var element, var value]
                && element.ParameterType.IsAssignableFrom(target)
                && BindablePropertyOf(owner, setter.Name[3..]) is { } bindable)
            {
                yield return (setter.Name[3..], value.ParameterType, bindable);
            }
        }
    }

    internal static ValueKind KindOf(string name, Type type, out bool nullable, out string[]? enumValues)
    {
        enumValues = null;
        var underlying = Nullable.GetUnderlyingType(type);
        nullable = underlying is not null || !type.IsValueType;
        var t = underlying ?? type;

        if (t == typeof(string))
        {
            return name == "FontFamily" ? ValueKind.FontFamily : ValueKind.String; // MAUI names fonts with strings
        }

        if (t == typeof(bool))
        {
            return ValueKind.Bool;
        }

        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
            || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte))
        {
            return ValueKind.Int;
        }

        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal))
        {
            return ValueKind.Double;
        }

        if (t.IsEnum)
        {
            enumValues = Enum.GetNames(t);
            return ValueKind.Enum;
        }

        if (t == typeof(LayoutOptions))
        {
            enumValues = LayoutOptionNames;
            return ValueKind.Enum;
        }

        if (t == typeof(Thickness))
        {
            return ValueKind.Thickness;
        }

        if (t == typeof(CornerRadius))
        {
            return ValueKind.CornerRadius;
        }

        if (typeof(Brush).IsAssignableFrom(t))
        {
            return ValueKind.Brush;
        }

        if (t == typeof(MauiColor))
        {
            return ValueKind.Color;
        }

        if (t == typeof(GridLength))
        {
            return ValueKind.GridLength;
        }

        if (typeof(ImageSource).IsAssignableFrom(t))
        {
            return ValueKind.ImageSource;
        }

        if (t == typeof(object))
        {
            return ValueKind.Object;
        }

        return TextParsed.Contains(t) ? ValueKind.String : ValueKind.Unsupported;
    }

    private static string TypeName(Type type) =>
        Nullable.GetUnderlyingType(type) is { } inner ? (inner.FullName ?? inner.Name) + "?" : type.FullName ?? type.Name;
}
