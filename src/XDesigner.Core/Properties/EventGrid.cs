using XDesigner.Core.Metadata;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;

namespace XDesigner.Core.Properties;

/// <summary>One row of the Events tab (§9.3): an event and the handler method its XAML attribute names.</summary>
/// <param name="Handler">The method name written in the XAML (same on every selected element), or null.</param>
/// <param name="IsMixed">The selected elements name different handlers (or only some name one).</param>
public sealed record EventRow(string Name, string Category, int Order, EventInfoDto Info, bool IsAdvanced, string? Handler, bool IsMixed)
{
    public bool IsSet => Handler is not null || IsMixed;
}

/// <summary>Event categories and the advanced (plumbing) events, like <see cref="PropertyCategories"/> for properties.</summary>
public static class EventCategories
{
    public const string Common = "Common";
    public const string Pointer = "Mouse and touch";
    public const string Keyboard = "Keyboard";
    public const string Focus = "Focus";
    public const string Window = "Window";
    public const string Lifetime = "Lifetime and layout";
    public const string Other = "Other";

    public static readonly string[] Order = [Common, Pointer, Keyboard, Focus, Window, Lifetime, Other];

    private static readonly (string Category, string[] Names)[] Map =
    [
        (Common, ["Click", "IsCheckedChanged", "TextChanged", "SelectionChanged", "ValueChanged", "SelectedDateChanged",
            "SelectedTimeChanged", "SelectedDatesChanged", "Opened", "Closing", "Closed"]),
        (Pointer, ["PointerPressed", "PointerReleased", "PointerMoved", "PointerEntered", "PointerExited", "PointerWheelChanged",
            "Tapped", "DoubleTapped", "Holding", "PointerCaptureLost"]),
        (Keyboard, ["KeyDown", "KeyUp", "TextInput"]),
        (Focus, ["GotFocus", "LostFocus"]),
        (Window, ["Activated", "Deactivated", "PositionChanged", "Resized", "ScalingChanged"]),
        (Lifetime, ["Loaded", "Unloaded", "SizeChanged"]),
    ];

    private static readonly HashSet<string> Advanced = new(StringComparer.Ordinal)
    {
        "AttachedToVisualTree", "DetachedFromVisualTree", "AttachedToLogicalTree", "DetachedFromLogicalTree", "TemplateApplied",
        "ResourcesChanged", "ActualThemeVariantChanged", "PropertyChanged", "DataContextChanged", "Initialized",
        "EffectiveViewportChanged", "LayoutUpdated", "GettingFocus", "LosingFocus", "TextInputMethodClientRequested",
        "ContextRequested", "ContextCanceled", "AccessKeyPressed", "PointerCaptureLost",
    };

    private static readonly Dictionary<string, (string Category, int Order)> ByName =
        Map.SelectMany(m => m.Names.Select((n, i) => (n, m.Category, i))).GroupBy(x => x.n).ToDictionary(g => g.Key, g => (g.First().Category, g.First().i));

    public static string CategoryOf(string name) => ByName.TryGetValue(name, out var e) ? e.Category
        : name.StartsWith("Pointer", StringComparison.Ordinal) || name.StartsWith("Pinch", StringComparison.Ordinal) || name.StartsWith("Scroll", StringComparison.Ordinal) ? Pointer
        : name.StartsWith("Key", StringComparison.Ordinal) || name.StartsWith("TextInput", StringComparison.Ordinal) ? Keyboard
        : Other;

    public static int OrderOf(string name) => ByName.TryGetValue(name, out var e) ? e.Order : int.MaxValue;

    public static bool IsAdvanced(string name) => Advanced.Contains(name);
}

public static class EventGridBuilder
{
    /// <summary>Rows for the events every selected element has; handler names as written in the XAML.</summary>
    public static IReadOnlyList<EventRow> Build(XamlDocument document, ControlCatalog catalog, IReadOnlyList<int> ids,
        Func<string, IReadOnlyList<EventInfoDto>?> eventsOf)
    {
        var nodes = ids.Select(document.FindById).OfType<ElementNode>().ToList();
        if (nodes.Count == 0)
        {
            return [];
        }

        var perNode = nodes.Select(n => (Node: n, Events: (catalog.Resolve(n) is { } t ? eventsOf(t.FullName) : null) ?? [])).ToList();
        var common = perNode[0].Events.Where(e => perNode.Skip(1).All(p => p.Events.Any(o => o.Name == e.Name))).ToList();

        return common
            .Select(e =>
            {
                var handlers = perNode.Select(p => (string?)p.Node.Element.Attribute(e.Name)).ToList();
                var same = handlers.All(h => h == handlers[0]);
                return new EventRow(e.Name, EventCategories.CategoryOf(e.Name), EventCategories.OrderOf(e.Name), e,
                    EventCategories.IsAdvanced(e.Name), same ? handlers[0] : null, !same);
            })
            .OrderBy(r => Array.IndexOf(EventCategories.Order, r.Category))
            .ThenBy(r => r.Order)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
    }
}
