using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Metadata;
using XDesigner.Protocol.Metadata;

namespace XDesigner.RenderHost.Metadata;

/// <summary>
/// Describes every public <see cref="Control"/> type in the loaded assemblies: Avalonia's, NuGet packages',
/// and the user's project (§14.3). Results are cached for the host's lifetime (the host restarts on rebuild).
/// </summary>
internal sealed class MetadataProvider
{
    /// <summary>Event a double-click creates a handler for, by type (base types are checked too).</summary>
    private static readonly Dictionary<string, string> DefaultEvents = new()
    {
        ["Avalonia.Controls.Button"] = "Click",
        ["Avalonia.Controls.MenuItem"] = "Click",
        ["Avalonia.Controls.Primitives.ToggleButton"] = "IsCheckedChanged",
        ["Avalonia.Controls.ToggleSwitch"] = "IsCheckedChanged",
        ["Avalonia.Controls.TextBox"] = "TextChanged",
        ["Avalonia.Controls.AutoCompleteBox"] = "TextChanged",
        ["Avalonia.Controls.Primitives.SelectingItemsControl"] = "SelectionChanged",
        ["Avalonia.Controls.TreeView"] = "SelectionChanged",
        ["Avalonia.Controls.Primitives.RangeBase"] = "ValueChanged",
        ["Avalonia.Controls.NumericUpDown"] = "ValueChanged",
        ["Avalonia.Controls.DatePicker"] = "SelectedDateChanged",
        ["Avalonia.Controls.CalendarDatePicker"] = "SelectedDateChanged",
        ["Avalonia.Controls.TimePicker"] = "SelectedTimeChanged",
        ["Avalonia.Controls.Calendar"] = "SelectedDatesChanged",
        ["Avalonia.Controls.Window"] = "Opened",
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
        var avaloniaNamespaces = AvaloniaClrNamespaces();
        var result = new List<ControlTypeInfo>();
        var types = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic))
        {
            foreach (var type in Hosting.UserAssemblyLoader.SafeGetTypes(assembly))
            {
                if (type.IsPublic && !type.IsGenericTypeDefinition && typeof(Control).IsAssignableFrom(type))
                {
                    result.Add(Describe(type, avaloniaNamespaces));
                    types.TryAdd(type.FullName ?? type.Name, type);
                }
            }
        }

        _types = types;
        return result.OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
    }

    private static ControlTypeInfo Describe(Type type, HashSet<(string Assembly, string ClrNamespace)> avaloniaNamespaces)
    {
        var assemblyName = type.Assembly.GetName().Name ?? "";
        var clrNamespace = type.Namespace ?? "";
        var xmlNamespace = avaloniaNamespaces.Contains((assemblyName, clrNamespace))
            ? ControlTypeInfo.AvaloniaXmlNamespace
            : $"using:{clrNamespace}";

        var creatable = !type.IsAbstract && !type.IsNested && type.GetConstructor(Type.EmptyTypes) is not null;
        var baseTypes = BaseTypes(type).Select(t => t.FullName ?? t.Name).ToArray();

        return new ControlTypeInfo(
            type.FullName ?? type.Name,
            type.Name,
            xmlNamespace,
            clrNamespace,
            assemblyName,
            assemblyName == "Avalonia" || assemblyName.StartsWith("Avalonia.", StringComparison.Ordinal),
            creatable,
            ContentModelOf(type),
            ContentPropertyOf(type)?.Name,
            DefaultEventOf(type),
            baseTypes,
            EventNamesOf(type));
    }

    private static IEnumerable<Type> BaseTypes(Type type)
    {
        for (var t = type.BaseType; t is not null && t != typeof(object); t = t.BaseType)
        {
            yield return t;
            if (t == typeof(Control))
            {
                yield break;
            }
        }
    }

    private static ContentModel ContentModelOf(Type type)
    {
        // A user's own UserControl/Window defines its content in its own .axaml file: never a drop target here.
        if ((typeof(UserControl).IsAssignableFrom(type) && type != typeof(UserControl))
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

        if (typeof(ContentControl).IsAssignableFrom(type))
        {
            return ContentModel.SingleContent;
        }

        if (typeof(Decorator).IsAssignableFrom(type))
        {
            return ContentModel.SingleChild;
        }

        var content = ContentPropertyOf(type);
        if (content is null)
        {
            return ContentModel.None;
        }

        if (typeof(InlineCollection).IsAssignableFrom(content.PropertyType))
        {
            return ContentModel.Inlines;
        }

        if (typeof(Control).IsAssignableFrom(content.PropertyType))
        {
            return ContentModel.SingleChild; // e.g. Viewbox.Child
        }

        if (content.PropertyType == typeof(object))
        {
            return ContentModel.SingleContent;
        }

        return typeof(Controls).IsAssignableFrom(content.PropertyType) ? ContentModel.Children : ContentModel.None;
    }

    private static PropertyInfo? ContentPropertyOf(Type type)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            var property = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .FirstOrDefault(p => p.GetCustomAttribute<ContentAttribute>() is not null);
            if (property is not null)
            {
                return property;
            }
        }

        return null;
    }

    private static string[] EventNamesOf(Type type)
    {
        var names = type.GetEvents(BindingFlags.Public | BindingFlags.Instance).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (var routed in Avalonia.Interactivity.RoutedEventRegistry.Instance.GetRegistered(t))
            {
                names.Add(routed.Name);
            }
        }

        return names.Order(StringComparer.Ordinal).ToArray();
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

    /// <summary>(assembly, CLR namespace) pairs mapped to the Avalonia XML namespace by <see cref="XmlnsDefinitionAttribute"/>.</summary>
    private static HashSet<(string, string)> AvaloniaClrNamespaces()
    {
        var set = new HashSet<(string, string)>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic))
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

            foreach (var definition in definitions.Where(d => d.XmlNamespace == ControlTypeInfo.AvaloniaXmlNamespace))
            {
                set.Add((assembly.GetName().Name ?? "", definition.ClrNamespace));
            }
        }

        return set;
    }
}
