using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using XDesigner.Core.Layout;
using XDesigner.Core.Metadata;
using XDesigner.Core.Naming;
using XDesigner.Core.Rendering;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;
using XDesigner.Protocol.Rendering;

namespace XDesigner.Core.Editing;

/// <summary>Copy and paste (§13).</summary>
public sealed partial class DesignerActions
{
    /// <summary>Namespace of the clipboard wrapper element.</summary>
    public const string FragmentNamespace = "urn:xdesigner:fragment";

    /// <summary>Clipboard format name the app registers alongside plain text.</summary>
    public const string ClipboardFormat = "xDesigner.Fragment.v1";

    private static readonly XNamespace Fragment = FragmentNamespace;

    /// <summary>
    /// Serializes the given elements (top-most only; children come along) as a fragment:
    /// <c>&lt;xd:Fragment xd:Source="…" xmlns…&gt;…&lt;/xd:Fragment&gt;</c>, carrying the document's namespace
    /// declarations so prefixes resolve wherever it's pasted. Returns null when nothing can be copied (only the root).
    /// </summary>
    public string? Copy(IEnumerable<int> ids)
    {
        var order = _document.Xml.Root!.DescendantsAndSelf().Select((e, i) => (e, i)).ToDictionary(x => x.e, x => x.i);
        var nodes = TopMost(ids).Where(n => n.Parent is not null).OrderBy(n => order[n.Element]).ToList();
        if (nodes.Count == 0)
        {
            return null;
        }

        var root = _document.Xml.Root!;
        var fragment = new XElement(Fragment + "Fragment",
            new XAttribute(XNamespace.Xmlns + "xd", FragmentNamespace),
            new XAttribute(Fragment + "Source", _document.InstanceId));
        foreach (var declaration in root.Attributes().Where(a => a.IsNamespaceDeclaration && a.Name.LocalName != "xd"))
        {
            fragment.Add(new XAttribute(declaration));
        }

        foreach (var node in nodes)
        {
            var clone = new XElement(node.Element);
            XamlFormatting.Reindent(clone, "", "  ");
            fragment.Add(clone);
        }

        return fragment.ToString(SaveOptions.None);
    }

    /// <summary>
    /// Pastes clipboard text (a designer fragment, or a plain XAML element) into the selected container or the nearest
    /// one above <paramref name="selectedId"/> that can take it. Names that clash get new ones, event handlers are
    /// dropped when the fragment came from another document, attached properties of other panel kinds are removed,
    /// and positioned elements are offset by 10 DIPs so they don't sit exactly on the originals.
    /// </summary>
    /// <returns>Ids of the pasted elements, or null with <paramref name="problem"/> set.</returns>
    public IReadOnlyList<int>? Paste(string text, int? selectedId, LayoutIndex layout, ControlCatalog catalog, out string? problem)
    {
        XElement parsed;
        try
        {
            parsed = XElement.Parse(text, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            problem = "The clipboard doesn't contain controls.";
            return null;
        }

        var isFragment = parsed.Name == Fragment + "Fragment";
        var elements = isFragment ? parsed.Elements().ToList() : [parsed];
        if (!isFragment)
        {
            GiveDefaultNamespace(parsed);
        }

        var sameDocument = isFragment && (string?)parsed.Attribute(Fragment + "Source") == _document.InstanceId;
        if (elements.Count == 0)
        {
            problem = "The clipboard doesn't contain controls.";
            return null;
        }

        // Target: the selection, or the nearest ancestor that can take children.
        var start = (selectedId is { } sid ? _document.FindById(sid) : null) ?? _document.Root;
        ElementNode? target = null;
        ControlTypeInfo? targetType = null;
        for (var node = start; node is not null; node = node.Parent)
        {
            if (catalog.Resolve(node) is { } type && DropPlanner.Accepts(node, type) && layout.Find(node.Id) is not null)
            {
                target = node;
                targetType = type;
                break;
            }
        }

        if (target is null || targetType is null)
        {
            problem = "Select a panel (Grid, StackPanel, Canvas…) to paste into.";
            return null;
        }

        if (targetType.Content is ContentModel.SingleContent or ContentModel.SingleChild && elements.Count > 1)
        {
            problem = $"{target.DisplayName} can hold only one control.";
            return null;
        }

        var targetKind = layout.Find(target.Id)?.Panel?.Kind;
        var root = _document.Xml.Root!;
        var commands = new List<IUndoableCommand>();
        var declared = new HashSet<XNamespace>();
        var assignedNames = new HashSet<string>(StringComparer.Ordinal);
        var unit = XamlFormatting.IndentUnit(_document.Xml);
        var indent = XamlFormatting.ChildIndent(target.Element);
        var clones = new List<XElement>();

        foreach (var element in elements)
        {
            var clone = new XElement(element);
            foreach (var declaration in clone.DescendantsAndSelf().SelectMany(e => e.Attributes()).Where(a => a.IsNamespaceDeclaration).ToList())
            {
                declaration.Remove(); // declared once on the root instead
            }

            if (!sameDocument)
            {
                RemoveEventHandlers(clone, catalog);
            }

            RenameClashes(clone, assignedNames);
            NormalizeForTarget(clone, targetKind);
            XamlFormatting.Reindent(clone, indent, unit);

            foreach (var ns in UsedNamespaces(clone))
            {
                if (root.GetPrefixOfNamespace(ns) is null && root.GetDefaultNamespace() != ns && declared.Add(ns))
                {
                    var prefix = UniquePrefix(root, (isFragment ? parsed.GetPrefixOfNamespace(ns) : null) ?? "local");
                    commands.Add(new SetAttributeCommand(_document.Root.Id, XNamespace.Xmlns + prefix, ns.NamespaceName));
                }
            }

            commands.Add(new InsertElementCommand(target.Id, clone, int.MaxValue, "Paste"));
            clones.Add(clone);
        }

        _undo.Execute(new CompositeCommand(clones.Count == 1 ? $"Paste {clones[0].Name.LocalName}" : $"Paste {clones.Count} controls", commands));
        problem = null;
        return clones.Select(c => _document.NodeFor(c).Id).ToList();
    }

    private IEnumerable<ElementNode> TopMost(IEnumerable<int> ids)
    {
        var nodes = ids.Select(_document.FindById).OfType<ElementNode>().ToList();
        var set = nodes.Select(n => n.Id).ToHashSet();
        return nodes.Where(n => !n.AncestorsAndSelf().Skip(1).Any(a => set.Contains(a.Id)));
    }

    /// <summary>A plain pasted snippet written without xmlns gets the Avalonia namespace.</summary>
    private static void GiveDefaultNamespace(XElement element)
    {
        foreach (var e in element.DescendantsAndSelf().Where(e => e.Name.Namespace == XNamespace.None))
        {
            e.Name = (XNamespace)ControlTypeInfo.AvaloniaXmlNamespace + e.Name.LocalName;
        }
    }

    private static void RemoveEventHandlers(XElement clone, ControlCatalog catalog)
    {
        foreach (var element in clone.DescendantsAndSelf())
        {
            if (catalog.Resolve(element.Name.NamespaceName, element.Name.LocalName) is not { } type)
            {
                continue;
            }

            foreach (var attribute in element.Attributes().Where(a => a.Name.Namespace == XNamespace.None && !a.Name.LocalName.Contains('.') && type.HasEvent(a.Name.LocalName)).ToList())
            {
                attribute.Remove();
            }
        }
    }

    private void RenameClashes(XElement clone, HashSet<string> assigned)
    {
        var used = NameGenerator.UsedNames(_document);
        foreach (var element in clone.DescendantsAndSelf())
        {
            var attribute = element.Attribute(XamlNs + "Name") ?? element.Attribute("Name");
            if (attribute is null)
            {
                continue;
            }

            if (used.Contains(attribute.Value) || assigned.Contains(attribute.Value))
            {
                attribute.Value = NameGenerator.Next(_document, element.Name.LocalName, assigned);
            }

            assigned.Add(attribute.Value);
        }
    }

    /// <summary>Drops other panels' attached properties and, where positions apply, offsets them by 10.</summary>
    private static void NormalizeForTarget(XElement element, PanelKind? targetKind)
    {
        foreach (var (attribute, owner) in PanelAttachedProperties)
        {
            if (owner != targetKind)
            {
                element.Attribute(attribute)?.Remove();
            }
        }

        var designerPositioned = (string?)element.Attribute("HorizontalAlignment") == "Left"
            && (string?)element.Attribute("VerticalAlignment") == "Top"
            && element.Attribute("Margin") is not null;

        switch (targetKind)
        {
            case PanelKind.Canvas:
                Offset(element, "Canvas.Left");
                Offset(element, "Canvas.Top");
                break;
            case PanelKind.Grid or PanelKind.Other when designerPositioned:
                var parts = ((string)element.Attribute("Margin")!).Split(',');
                if (parts.Length == 4 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var l)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                {
                    element.SetAttributeValue("Margin", string.Create(CultureInfo.InvariantCulture, $"{l + 10},{t + 10},{parts[2]},{parts[3]}"));
                }

                break;
            default:
                if (designerPositioned && !IsPositional(targetKind))
                {
                    element.Attribute("HorizontalAlignment")?.Remove();
                    element.Attribute("VerticalAlignment")?.Remove();
                    element.Attribute("Margin")?.Remove();
                }

                break;
        }
    }

    private static void Offset(XElement element, string attribute)
    {
        if (double.TryParse((string?)element.Attribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            element.SetAttributeValue(attribute, (value + 10).ToString(CultureInfo.InvariantCulture));
        }
    }

    private static IEnumerable<XNamespace> UsedNamespaces(XElement element) =>
        element.DescendantsAndSelf()
            .SelectMany(e => e.Attributes().Select(a => a.Name.Namespace).Prepend(e.Name.Namespace))
            .Where(ns => ns != XNamespace.None && ns != XNamespace.Xml && ns != XNamespace.Xmlns)
            .Distinct();

    private static string UniquePrefix(XElement root, string wanted)
    {
        var used = root.Attributes().Where(a => a.IsNamespaceDeclaration).Select(a => a.Name.LocalName).ToHashSet();
        var prefix = wanted;
        for (var n = 2; used.Contains(prefix); n++)
        {
            prefix = wanted + n;
        }

        return prefix;
    }
}
