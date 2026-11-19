using System.Collections.Concurrent;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using XDesigner.Protocol.Metadata;

namespace XDesigner.RenderHost.Metadata;

/// <summary>One property as the host knows it: the DTO sent to the app plus how to read it from a live control.</summary>
internal sealed record HostProperty(PropertyInfoDto Info, AvaloniaProperty? Avalonia, PropertyInfo? Clr);

/// <summary>
/// The properties a control type can set from XAML (§14.3): registered Avalonia properties (own and inherited),
/// settable CLR properties of simple types, and attached properties of the common owners. Cached per type.
/// </summary>
internal sealed class PropertyMetadataProvider
{
    /// <summary>Owners whose attached properties are offered on every control (the app shows only the relevant ones).</summary>
    private static readonly Type[] AttachedOwners = [typeof(Grid), typeof(Canvas), typeof(DockPanel), typeof(ToolTip), typeof(ScrollViewer)];

    /// <summary>Types XAML parses from text, so a text box is a fine editor.</summary>
    private static readonly HashSet<Type> TextParsed =
    [
        typeof(Point), typeof(Size), typeof(Vector), typeof(Rect), typeof(RelativePoint), typeof(TimeSpan),
        typeof(DateTime), typeof(DateTimeOffset), typeof(Uri), typeof(char), typeof(Cursor), typeof(KeyGesture),
        typeof(Geometry), typeof(RowDefinitions), typeof(ColumnDefinitions),
    ];

    private readonly ConcurrentDictionary<Type, HostProperty[]> _cache = new();

    public HostProperty[] For(Type type) => _cache.GetOrAdd(type, Describe);

    private static HostProperty[] Describe(Type type)
    {
        var result = new Dictionary<string, HostProperty>(StringComparer.Ordinal);

        // Avalonia properties are registered by static constructors: make sure they ran for the type, its bases
        // and the attached-property owners, even if none of them has been instantiated yet.
        foreach (var t in AttachedOwners.Concat(SelfAndBases(type)))
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(t.TypeHandle);
        }

        // Avalonia properties: when a name is registered more than once (a subclass re-registers), keep the most derived.
        var registered = AvaloniaPropertyRegistry.Instance.GetRegistered(type)
            .Where(p => !p.IsReadOnly)
            .GroupBy(p => p.Name)
            .Select(g => g.OrderByDescending(p => Depth(p.OwnerType)).First());
        foreach (var property in registered)
        {
            var kind = KindOf(property.PropertyType, out var nullable, out var enumValues);
            result[property.Name] = new HostProperty(
                new PropertyInfoDto(property.Name, property.OwnerType.Name, TypeName(property.PropertyType), kind, false, nullable, enumValues),
                property,
                null);
        }

        // Plain CLR properties with a public setter that XAML can set from text.
        foreach (var clr in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (result.ContainsKey(clr.Name) || clr.SetMethod is not { IsPublic: true } || clr.GetIndexParameters().Length > 0
                || clr.DeclaringType is null || clr.DeclaringType.Assembly == typeof(object).Assembly)
            {
                continue;
            }

            var kind = KindOf(clr.PropertyType, out var nullable, out var enumValues);
            if (kind is ValueKind.Unsupported or ValueKind.Object)
            {
                continue;
            }

            result[clr.Name] = new HostProperty(
                new PropertyInfoDto(clr.Name, clr.DeclaringType.Name, TypeName(clr.PropertyType), kind, false, nullable, enumValues),
                null,
                clr);
        }

        if (typeof(Control).IsAssignableFrom(type))
        {
            foreach (var owner in AttachedOwners)
            {
                foreach (var property in AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(owner).Where(p => p.OwnerType == owner && !p.IsReadOnly))
                {
                    var kind = KindOf(property.PropertyType, out var nullable, out var enumValues);
                    var info = new PropertyInfoDto(property.Name, owner.Name, TypeName(property.PropertyType), kind, true, nullable, enumValues);
                    result.TryAdd(info.Key, new HostProperty(info, property, null));
                }
            }
        }

        return result.Values.OrderBy(p => p.Info.Key, StringComparer.Ordinal).ToArray();
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

        if (typeof(IBrush).IsAssignableFrom(t))
        {
            return ValueKind.Brush;
        }

        if (t == typeof(Color))
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

        if (typeof(IImage).IsAssignableFrom(t) || t == typeof(WindowIcon))
        {
            return ValueKind.ImageSource;
        }

        if (t == typeof(object))
        {
            return ValueKind.Object;
        }

        return TextParsed.Contains(t) ? ValueKind.String : ValueKind.Unsupported;
    }

    private static IEnumerable<Type> SelfAndBases(Type type)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            yield return t;
        }
    }

    private static int Depth(Type type)
    {
        var depth = 0;
        for (var t = type; t is not null; t = t.BaseType)
        {
            depth++;
        }

        return depth;
    }

    private static string TypeName(Type type) =>
        Nullable.GetUnderlyingType(type) is { } inner ? (inner.FullName ?? inner.Name) + "?" : type.FullName ?? type.Name;
}
