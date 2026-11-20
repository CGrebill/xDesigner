using System.Collections;
using System.Reflection;
using XDesigner.Protocol.Metadata;
using XDesigner.RenderHost.Rendering;

namespace XDesigner.RenderHost.Metadata;

/// <summary>
/// Describes every public visual element type in the loaded assemblies: MAUI's, packages', and the user's project
/// (§14.3, §14.6). Cached for the host's lifetime.
/// </summary>
internal sealed class MetadataProvider(MauiTypeResolver resolver)
{
    /// <summary>Event a double-click creates a handler for, by type (base types are checked too).</summary>
    private static readonly Dictionary<string, string> DefaultEvents = new()
    {
        ["Microsoft.Maui.Controls.Button"] = "Clicked",
        ["Microsoft.Maui.Controls.ImageButton"] = "Clicked",
        ["Microsoft.Maui.Controls.InputView"] = "TextChanged",
        ["Microsoft.Maui.Controls.SearchBar"] = "SearchButtonPressed",
        ["Microsoft.Maui.Controls.Switch"] = "Toggled",
        ["Microsoft.Maui.Controls.CheckBox"] = "CheckedChanged",
        ["Microsoft.Maui.Controls.RadioButton"] = "CheckedChanged",
        ["Microsoft.Maui.Controls.Slider"] = "ValueChanged",
        ["Microsoft.Maui.Controls.Stepper"] = "ValueChanged",
        ["Microsoft.Maui.Controls.Picker"] = "SelectedIndexChanged",
        ["Microsoft.Maui.Controls.DatePicker"] = "DateSelected",
        ["Microsoft.Maui.Controls.SelectableItemsView"] = "SelectionChanged",
        ["Microsoft.Maui.Controls.Page"] = "Appearing",
    };

    private ControlTypeInfo[]? _cache;
    private Dictionary<string, Type> _types = [];

    public ControlTypeInfo[] GetControlTypes() => _cache ??= Scan();

    /// <summary>A control type by full name (one of those <see cref="GetControlTypes"/> reports).</summary>
    public Type? FindType(string fullName)
    {
        GetControlTypes();
        return _types.GetValueOrDefault(fullName);
    }

    private ControlTypeInfo[] Scan()
    {
        var result = new List<ControlTypeInfo>();
        var types = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic))
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                if (type is { IsPublic: true, IsGenericTypeDefinition: false }
                    && (typeof(VisualElement).IsAssignableFrom(type) || typeof(Window).IsAssignableFrom(type))
                    && types.TryAdd(type.FullName ?? type.Name, type))
                {
                    result.Add(Describe(type));
                }
            }
        }

        _types = types;
        return result.OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
    }

    private ControlTypeInfo Describe(Type type)
    {
        var assemblyName = type.Assembly.GetName().Name ?? "";
        var clrNamespace = type.Namespace ?? "";
        var builtIn = MauiRenderer.IsMauiType(type);
        var content = ContentPropertyOf(type);
        return new ControlTypeInfo(
            type.FullName ?? type.Name,
            type.Name,
            resolver.IsMappedTo(MauiTypeResolver.MauiNamespace, type) ? MauiTypeResolver.MauiNamespace : $"clr-namespace:{clrNamespace};assembly={assemblyName}",
            clrNamespace,
            assemblyName,
            builtIn,
            !type.IsAbstract && !type.IsNested && type.GetConstructor(Type.EmptyTypes) is not null,
            ContentModelOf(type, content),
            content?.Name,
            DefaultEventOf(type),
            BaseTypes(type).Select(t => t.FullName ?? t.Name).ToArray(),
            type.GetEvents(BindingFlags.Public | BindingFlags.Instance).Select(e => e.Name).Distinct().Order(StringComparer.Ordinal).ToArray());
    }

    internal static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<Type> BaseTypes(Type type)
    {
        for (var t = type.BaseType; t is not null && t != typeof(object) && t != typeof(BindableObject); t = t.BaseType)
        {
            yield return t;
        }
    }

    private static ContentModel ContentModelOf(Type type, PropertyInfo? content)
    {
        // A user's own ContentView/ContentPage defines its content in its own .xaml file: never a drop target here.
        if (!MauiRenderer.IsMauiType(type) && (typeof(ContentView).IsAssignableFrom(type) || typeof(Page).IsAssignableFrom(type)))
        {
            return ContentModel.None;
        }

        if (typeof(Layout).IsAssignableFrom(type))
        {
            return ContentModel.Children;
        }

        if (content is null)
        {
            return ContentModel.None;
        }

        var propertyType = content.PropertyType;
        if (typeof(View).IsAssignableFrom(propertyType) || propertyType == typeof(object))
        {
            return content.Name == "Content" ? ContentModel.SingleContent : ContentModel.SingleChild;
        }

        if (propertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(propertyType)
            && propertyType.GetInterfaces().Concat([propertyType]).Any(i => i.IsGenericType && i.GetGenericArguments() is [var item] && typeof(View).IsAssignableFrom(item)))
        {
            return ContentModel.Children;
        }

        return ContentModel.None; // Label.Text, Picker.Items of strings…
    }

    /// <summary>The property named by <c>[ContentProperty("…")]</c> on the type or a base type.</summary>
    internal static PropertyInfo? ContentPropertyOf(Type type)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            var attribute = t.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.Name == "ContentPropertyAttribute");
            if (attribute?.ConstructorArguments is [{ Value: string name }])
            {
                // The most derived one: a subclass may redeclare it with "new" (Compatibility.AbsoluteLayout.Children).
                return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.Name == name)
                    .MaxBy(p => Depth(p.DeclaringType));
            }
        }

        return null;
    }

    private static int Depth(Type? type)
    {
        var depth = 0;
        for (var t = type; t is not null; t = t.BaseType)
        {
            depth++;
        }

        return depth;
    }

    private static string? DefaultEventOf(Type type)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            if (t.FullName is { } name && DefaultEvents.TryGetValue(name, out var evt) && type.GetEvent(evt) is not null)
            {
                return evt;
            }
        }

        return type.GetEvent("Loaded") is not null ? "Loaded" : null;
    }
}
