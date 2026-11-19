using System.Xml.Linq;
using XDesigner.Core.Metadata;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;

namespace XDesigner.Core.Properties;

/// <summary>How a property is written in the XAML of the selected element(s).</summary>
public enum PropertyValueForm
{
    /// <summary>Not in the XAML: the effective value comes from defaults, styles or inheritance.</summary>
    NotSet,

    /// <summary>A plain attribute (<c>Background="Red"</c>): editable.</summary>
    Attribute,

    /// <summary>A binding or resource (<c>{Binding Name}</c>): shown read-only, can be reset (D7).</summary>
    Markup,

    /// <summary>A property element (<c>&lt;Button.Background&gt;…</c>): shown read-only, can be reset.</summary>
    PropertyElement,

    /// <summary>Text inside the element (<c>&lt;Button&gt;OK&lt;/Button&gt;</c>): editable in place.</summary>
    TextContent,

    /// <summary>Child controls are the content: shown as a link to the child.</summary>
    ChildElements,
}

/// <summary>One row of the properties window, for one element or the common properties of several (§9.2).</summary>
/// <param name="Key">Attribute name: <c>Background</c> or <c>Grid.Row</c>.</param>
/// <param name="Info">Metadata from the renderer; null for attributes the renderer doesn't know (<see cref="PropertyCategories.Other"/>).</param>
/// <param name="LocalValue">The value as written (escape removed), when <see cref="Form"/> is Attribute/TextContent/Markup and not mixed.</param>
/// <param name="IsMixed">The selected elements differ in how or what is set.</param>
/// <param name="EffectiveValue">What the renderer reports, when all selected elements agree.</param>
/// <param name="Note">Text for read-only forms: the binding, "set in XAML", or the child control's name.</param>
/// <param name="LinkedElementId">For <see cref="PropertyValueForm.ChildElements"/>: the child to select.</param>
public sealed record PropertyRow(
    string Key,
    string Category,
    int Order,
    ValueKind Kind,
    PropertyInfoDto? Info,
    bool IsAdvanced,
    PropertyValueForm Form,
    string? LocalValue,
    bool IsMixed,
    string? EffectiveValue,
    ValueSource? EffectiveSource,
    string? Note = null,
    int? LinkedElementId = null)
{
    /// <summary>Set in the XAML of at least one selected element (label shown bold, Reset available).</summary>
    public bool IsSet => Form != PropertyValueForm.NotSet;

    /// <summary>Bindings, property elements, child controls and unsupported types are shown, never edited.</summary>
    public bool IsReadOnly =>
        Kind == ValueKind.Unsupported || Form is PropertyValueForm.Markup or PropertyValueForm.PropertyElement or PropertyValueForm.ChildElements;

    public string[]? EnumValues => Info?.EnumValues;
}

/// <summary>Builds the rows of the properties window from the XAML (local values) and the renderer (types, effective values).</summary>
public static class PropertyGridBuilder
{
    /// <summary>The property shown in the header instead of as a row.</summary>
    public const string NameKey = "Name";

    /// <summary>
    /// Rows for the elements <paramref name="ids"/>: for several elements, only the properties all of them have,
    /// with mixed values left empty (§9.2). Attached properties appear only where the parent uses them.
    /// </summary>
    /// <param name="propertiesOf">Properties of a type by full name; null while still loading.</param>
    /// <param name="valuesOf">Effective values of an element by id; null when unknown.</param>
    public static IReadOnlyList<PropertyRow> Build(
        XamlDocument document,
        ControlCatalog catalog,
        IReadOnlyList<int> ids,
        Func<string, IReadOnlyList<PropertyInfoDto>?> propertiesOf,
        Func<int, IReadOnlyDictionary<string, PropertyValueDto>?> valuesOf)
    {
        var nodes = ids.Select(document.FindById).OfType<ElementNode>().ToList();
        if (nodes.Count == 0)
        {
            return [];
        }

        var perNode = new List<(ElementNode Node, ControlTypeInfo? Type, Dictionary<string, PropertyInfoDto> Props)>();
        foreach (var node in nodes)
        {
            var type = catalog.Resolve(node);
            var parent = node.Parent is { } p ? catalog.Resolve(p) : null;
            var props = (type is null ? null : propertiesOf(type.FullName)) ?? [];
            perNode.Add((node, type, props
                .Where(pr => pr.Key != NameKey && (PropertyCategories.AppliesTo(pr, parent, catalog) || IsWritten(node.Element, pr.Key)))
                .GroupBy(pr => pr.Key)
                .ToDictionary(g => g.Key, g => g.First())));
        }

        // Properties every selected element has, with the same kind of value.
        var first = perNode[0].Props;
        var common = first.Values
            .Where(pr => perNode.Skip(1).All(n => n.Props.TryGetValue(pr.Key, out var other) && other.Kind == pr.Kind))
            .ToList();

        var rows = new List<PropertyRow>();
        foreach (var info in common)
        {
            var states = perNode.Select(n => StateOf(n.Node, n.Type, info.Key, info, valuesOf(n.Node.Id))).ToList();
            rows.Add(Combine(info.Key, info.Kind, info, states));
        }

        if (nodes.Count == 1)
        {
            rows.AddRange(UnknownAttributes(perNode[0].Node, perNode[0].Type, first));
        }

        return rows
            .OrderBy(r => Array.IndexOf(PropertyCategories.Order, r.Category))
            .ThenBy(r => r.Order)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The property that text written inside an element sets: <c>Content</c> for a Button, <c>Text</c> for a TextBlock.</summary>
    public static string? TextContentKey(ControlTypeInfo? type) => type?.Content switch
    {
        ContentModel.Inlines => "Text",
        ContentModel.SingleContent or ContentModel.SingleChild => type.ContentPropertyName,
        _ => null,
    };

    /// <summary>Direct, non-whitespace text of an element (<c>&lt;Button&gt;OK&lt;/Button&gt;</c>).</summary>
    public static string? TextContentOf(XElement element)
    {
        var text = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>The property element for <paramref name="key"/> on an element, e.g. <c>&lt;Button.Background&gt;</c>.</summary>
    public static XElement? PropertyElementOf(XElement element, string key, string? ownerName = null) =>
        element.Elements().FirstOrDefault(e =>
            e.Name.LocalName == key
            || e.Name.LocalName == $"{element.Name.LocalName}.{key}"
            || (ownerName is not null && e.Name.LocalName == $"{ownerName}.{key}"));

    private static bool IsWritten(XElement element, string key) => element.Attribute(key) is not null;

    private readonly record struct NodeState(PropertyValueForm Form, string? Local, string? Effective, ValueSource? Source, string? Note, int? Linked);

    private static NodeState StateOf(ElementNode node, ControlTypeInfo? type, string key, PropertyInfoDto? info, IReadOnlyDictionary<string, PropertyValueDto>? values)
    {
        var element = node.Element;
        var effective = values?.GetValueOrDefault(key);
        string? eff = effective?.Value;
        ValueSource? src = effective?.Source;

        if (element.Attribute(key) is { } attribute)
        {
            return XamlValueSerializer.IsMarkupExtension(attribute.Value)
                ? new NodeState(PropertyValueForm.Markup, attribute.Value, eff, src, attribute.Value, null)
                : new NodeState(PropertyValueForm.Attribute, XamlValueSerializer.FromAttribute(attribute.Value), eff, src, null, null);
        }

        var contentKey = TextContentKey(type);
        if (PropertyElementOf(element, key, info?.OwnerName) is { } propertyElement)
        {
            if (key == type?.ContentPropertyName && propertyElement.Elements().FirstOrDefault(RenderCopyBuilder.IsObjectElement) is { } child)
            {
                var childNode = node.Document.NodeFor(child);
                return new NodeState(PropertyValueForm.ChildElements, null, eff, src, childNode.DisplayName, childNode.Id);
            }

            return new NodeState(PropertyValueForm.PropertyElement, null, eff, src, $"Set in XAML as <{propertyElement.Name.LocalName}>", null);
        }

        if (key == contentKey)
        {
            if (type!.Content != ContentModel.Inlines && node.Children.Count > 0 && node.Children[0] is var child)
            {
                return new NodeState(PropertyValueForm.ChildElements, null, eff, src, child.DisplayName, child.Id);
            }

            if (type.Content == ContentModel.Inlines && element.Elements().Any(RenderCopyBuilder.IsObjectElement))
            {
                return new NodeState(PropertyValueForm.PropertyElement, null, eff, src, "Formatted text (runs) in XAML", null);
            }

            if (TextContentOf(element) is { } text)
            {
                return new NodeState(PropertyValueForm.TextContent, text, eff, src, null, null);
            }
        }

        return new NodeState(PropertyValueForm.NotSet, null, eff, src, null, null);
    }

    private static PropertyRow Combine(string key, ValueKind kind, PropertyInfoDto? info, List<NodeState> states)
    {
        var category = info is null ? PropertyCategories.Other : PropertyCategories.CategoryOf(key);
        var order = PropertyCategories.OrderOf(key);
        var advanced = info is not null && PropertyCategories.IsAdvanced(key, kind);
        var s0 = states[0];

        var sameForm = states.All(s => s.Form == s0.Form);
        var sameLocal = sameForm && states.All(s => s.Local == s0.Local);
        var sameEffective = states.All(s => s.Effective == s0.Effective);

        if (sameLocal)
        {
            return new PropertyRow(key, category, order, kind, info, advanced, s0.Form, s0.Local, false,
                sameEffective ? s0.Effective : null, sameEffective ? s0.Source : null, s0.Note, states.Count == 1 ? s0.Linked : null);
        }

        // Mixed: editable if every element is either unset or a plain value; otherwise read-only.
        var editable = states.All(s => s.Form is PropertyValueForm.NotSet or PropertyValueForm.Attribute or PropertyValueForm.TextContent);
        var form = editable
            ? states.FirstOrDefault(s => s.Form != PropertyValueForm.NotSet).Form
            : states.First(s => s.Form is PropertyValueForm.Markup or PropertyValueForm.PropertyElement or PropertyValueForm.ChildElements).Form;
        return new PropertyRow(key, category, order, kind, info, advanced, form, null, true,
            sameEffective ? s0.Effective : null, sameEffective ? s0.Source : null, editable ? null : "Different values");
    }

    /// <summary>Attributes the renderer doesn't describe (unknown attached properties, typos): read-only, but resettable.</summary>
    private static IEnumerable<PropertyRow> UnknownAttributes(ElementNode node, ControlTypeInfo? type, Dictionary<string, PropertyInfoDto> known)
    {
        foreach (var attribute in node.Element.Attributes())
        {
            var name = attribute.Name;
            if (attribute.IsNamespaceDeclaration || name.Namespace != XNamespace.None || name.LocalName == NameKey
                || known.ContainsKey(name.LocalName) || type?.HasEvent(name.LocalName) == true || IsAttachedEvent(name.LocalName))
            {
                continue;
            }

            yield return new PropertyRow(name.LocalName, PropertyCategories.Other, int.MaxValue, ValueKind.Unsupported, null, false,
                PropertyValueForm.Attribute, XamlValueSerializer.FromAttribute(attribute.Value), false, null, null);
        }
    }

    // Button.Click on a non-Button is an attached handler (shown in the Events tab instead).
    private static bool IsAttachedEvent(string name) =>
        name.Split('.') is [_, var member] && (member.EndsWith("Click", StringComparison.Ordinal) || member.EndsWith("Changed", StringComparison.Ordinal)
            || member.StartsWith("Pointer", StringComparison.Ordinal) || member.StartsWith("Key", StringComparison.Ordinal));
}
