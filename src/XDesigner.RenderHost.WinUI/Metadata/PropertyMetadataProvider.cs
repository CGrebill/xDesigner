using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XDesigner.Protocol.Metadata;

namespace XDesigner.RenderHost.Metadata;

/// <summary>One property as the host knows it: the DTO sent to the app plus how to read it from a live element.</summary>
internal sealed record HostProperty(PropertyInfoDto Info, DependencyProperty? Dependency, PropertyInfo? Clr);

/// <summary>
/// The properties an element type can set from XAML (§14.3): dependency properties (a settable CLR property with a static
/// <c>{Name}Property</c>), other settable CLR properties of simple types, and attached properties of the common owners.
/// Cached per type. UI thread only (reading a DependencyProperty needs the XAML runtime).
/// </summary>
internal sealed class PropertyMetadataProvider
{
    /// <summary>Owners whose attached properties are offered on every element (the app shows only the relevant ones).</summary>
    private static readonly Type[] AttachedOwners =
        [typeof(Grid), typeof(Canvas), typeof(RelativePanel), typeof(VariableSizedWrapGrid), typeof(ToolTipService), typeof(ScrollViewer)];

    /// <summary>Types XAML parses from text, so a text box is a fine editor.</summary>
    private static readonly HashSet<Type> TextParsed =
    [
        typeof(Windows.Foundation.Point), typeof(Windows.Foundation.Size), typeof(Windows.Foundation.Rect), typeof(TimeSpan),
        typeof(DateTime), typeof(DateTimeOffset), typeof(Uri), typeof(char), typeof(Windows.UI.Text.FontWeight), typeof(Duration),
        typeof(Geometry),
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

            var kind = KindOf(clr.PropertyType, out var nullable, out var enumValues);
            var dependency = DependencyPropertyOf(type, clr.Name);
            if (dependency is null && kind is ValueKind.Unsupported or ValueKind.Object)
            {
                continue; // a plain CLR property XAML text can't set
            }

            result[clr.Name] = new HostProperty(
                new PropertyInfoDto(clr.Name, clr.DeclaringType.Name, TypeName(clr.PropertyType), kind, false, nullable, enumValues),
                dependency,
                dependency is null ? clr : null);
        }

        if (typeof(UIElement).IsAssignableFrom(type))
        {
            foreach (var owner in AttachedOwners)
            {
                foreach (var (name, valueType, dependency) in AttachedProperties(owner, type))
                {
                    var kind = KindOf(valueType, out var nullable, out var enumValues);
                    var info = new PropertyInfoDto(name, owner.Name, TypeName(valueType), kind, true, nullable, enumValues);
                    result.TryAdd(info.Key, new HostProperty(info, dependency, null));
                }
            }
        }

        return result.Values.OrderBy(p => p.Info.Key, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The static <c>{name}Property</c> of a type or its bases (a field in some projections, a property in others).</summary>
    private static DependencyProperty? DependencyPropertyOf(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        try
        {
            return (type.GetProperty(name + "Property", flags)?.GetValue(null) ?? type.GetField(name + "Property", flags)?.GetValue(null)) as DependencyProperty;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Attached properties of <paramref name="owner"/> that can be set on <paramref name="target"/>: static <c>SetX(target, value)</c> with an <c>XProperty</c>.</summary>
    private static IEnumerable<(string Name, Type ValueType, DependencyProperty Dependency)> AttachedProperties(Type owner, Type target)
    {
        foreach (var setter in owner.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (setter.Name.StartsWith("Set", StringComparison.Ordinal) && setter.GetParameters() is [var element, var value]
                && element.ParameterType.IsAssignableFrom(target)
                && DependencyPropertyOf(owner, setter.Name[3..]) is { } dependency)
            {
                yield return (setter.Name[3..], value.ParameterType, dependency);
            }
        }
    }

    internal static ValueKind KindOf(Type type, out bool nullable, out string[]? enumValues)
    {
        enumValues = null;
        var underlying = Nullable.GetUnderlyingType(type);
        nullable = underlying is not null || !type.IsValueType;
        var t = underlying ?? type;

        if (t == typeof(string))
        {
            return ValueKind.String;
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

        if (t == typeof(Windows.UI.Color))
        {
            return ValueKind.Color;
        }

        if (t == typeof(FontFamily))
        {
            return ValueKind.FontFamily;
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
