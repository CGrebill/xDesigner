using System.Collections;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using XDesigner.Protocol.Metadata;
using XDesigner.RenderHost.Rendering;

namespace XDesigner.RenderHost.Metadata;

/// <summary>
/// Describes every public element type in the loaded assemblies: WinUI's (including the controls library), packages', and
/// the user's project (§14.3), plus <see cref="Window"/> for document roots. Cached for the host's lifetime.
/// </summary>
internal sealed class MetadataProvider
{
    /// <summary>Event a double-click creates a handler for, by type (base types are checked too).</summary>
    private static readonly Dictionary<string, string> DefaultEvents = new()
    {
        ["Microsoft.UI.Xaml.Controls.Primitives.ButtonBase"] = "Click",
        ["Microsoft.UI.Xaml.Controls.MenuFlyoutItem"] = "Click",
        ["Microsoft.UI.Xaml.Controls.ToggleSwitch"] = "Toggled",
        ["Microsoft.UI.Xaml.Controls.TextBox"] = "TextChanged",
        ["Microsoft.UI.Xaml.Controls.PasswordBox"] = "PasswordChanged",
        ["Microsoft.UI.Xaml.Controls.AutoSuggestBox"] = "TextChanged",
        ["Microsoft.UI.Xaml.Controls.RichEditBox"] = "TextChanged",
        ["Microsoft.UI.Xaml.Controls.Primitives.Selector"] = "SelectionChanged",
        ["Microsoft.UI.Xaml.Controls.TabView"] = "SelectionChanged",
        ["Microsoft.UI.Xaml.Controls.NavigationView"] = "SelectionChanged",
        ["Microsoft.UI.Xaml.Controls.RadioButtons"] = "SelectionChanged",
        ["Microsoft.UI.Xaml.Controls.TreeView"] = "ItemInvoked",
        ["Microsoft.UI.Xaml.Controls.Primitives.RangeBase"] = "ValueChanged",
        ["Microsoft.UI.Xaml.Controls.NumberBox"] = "ValueChanged",
        ["Microsoft.UI.Xaml.Controls.RatingControl"] = "ValueChanged",
        ["Microsoft.UI.Xaml.Controls.DatePicker"] = "SelectedDateChanged",
        ["Microsoft.UI.Xaml.Controls.CalendarDatePicker"] = "DateChanged",
        ["Microsoft.UI.Xaml.Controls.TimePicker"] = "SelectedTimeChanged",
        ["Microsoft.UI.Xaml.Controls.CalendarView"] = "SelectedDatesChanged",
        ["Microsoft.UI.Xaml.Controls.ColorPicker"] = "ColorChanged",
        ["Microsoft.UI.Xaml.Window"] = "Activated",
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
                    && (typeof(UIElement).IsAssignableFrom(type) || typeof(Window).IsAssignableFrom(type))
                    && types.TryAdd(type.FullName ?? type.Name, type))
                {
                    result.Add(Describe(type));
                }
            }
        }

        _types = types;
        return result.OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
    }

    private static ControlTypeInfo Describe(Type type)
    {
        var assemblyName = type.Assembly.GetName().Name ?? "";
        var clrNamespace = type.Namespace ?? "";
        var builtIn = XamlRenderer.IsWinUIType(type);
        var content = ContentPropertyOf(type);
        return new ControlTypeInfo(
            type.FullName ?? type.Name,
            type.Name,
            builtIn ? XamlTypeResolver.PresentationNamespace : $"using:{clrNamespace}",
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
        for (var t = type.BaseType; t is not null && t != typeof(object) && t != typeof(DependencyObject); t = t.BaseType)
        {
            yield return t;
        }
    }

    private static ContentModel ContentModelOf(Type type, PropertyInfo? content)
    {
        // A user's own UserControl/Page/Window defines its content in its own .xaml file: never a drop target here.
        if ((typeof(UserControl).IsAssignableFrom(type) && type != typeof(UserControl) && type != typeof(Page))
            || (typeof(Window).IsAssignableFrom(type) && type != typeof(Window)))
        {
            return ContentModel.None;
        }

        if (typeof(Panel).IsAssignableFrom(type))
        {
            return ContentModel.Children;
        }

        if (typeof(ItemsControl).IsAssignableFrom(type))
        {
            return ContentModel.Items;
        }

        if (typeof(ContentControl).IsAssignableFrom(type) || type == typeof(Window))
        {
            return ContentModel.SingleContent;
        }

        if (content is null)
        {
            return ContentModel.None;
        }

        var propertyType = content.PropertyType;
        if (typeof(InlineCollection).IsAssignableFrom(propertyType))
        {
            return ContentModel.Inlines;
        }

        if (typeof(UIElement).IsAssignableFrom(propertyType))
        {
            return ContentModel.SingleChild; // Border.Child, Viewbox.Child, UserControl.Content
        }

        if (propertyType == typeof(object))
        {
            return ContentModel.SingleContent;
        }

        if (typeof(UIElementCollection).IsAssignableFrom(propertyType))
        {
            return ContentModel.Children;
        }

        // TabView.TabItems, MenuBar.Items, NavigationView.MenuItems: lists of items.
        return propertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(propertyType) ? ContentModel.Items : ContentModel.None;
    }

    /// <summary>The property named by <c>[ContentProperty(Name = …)]</c> on the type or a base type.</summary>
    internal static PropertyInfo? ContentPropertyOf(Type type)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (var attribute in t.GetCustomAttributes(inherit: false))
            {
                if (attribute.GetType().Name == "ContentPropertyAttribute"
                    && (attribute.GetType().GetField("Name")?.GetValue(attribute) ?? attribute.GetType().GetProperty("Name")?.GetValue(attribute)) is string name
                    && type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is { } property)
                {
                    return property;
                }
            }
        }

        return null;
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
