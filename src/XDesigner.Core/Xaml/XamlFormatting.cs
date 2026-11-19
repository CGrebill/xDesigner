using System.Xml.Linq;

namespace XDesigner.Core.Xaml;

/// <summary>
/// Inserts elements with indentation that matches their surroundings (§5.5), and reports exactly which
/// nodes were added so undo can take them out again and leave the text as it was.
/// </summary>
public static class XamlFormatting
{
    private const string DefaultIndentUnit = "    ";

    /// <summary>
    /// Inserts <paramref name="child"/> into <paramref name="container"/> before its object child number
    /// <paramref name="index"/> (or last when <paramref name="index"/> is past the end).
    /// </summary>
    /// <returns>Every node added (the element and any whitespace), in document order.</returns>
    public static IReadOnlyList<XNode> InsertChild(XElement container, XElement child, int index)
    {
        var objectChildren = container.Elements().Where(RenderCopyBuilder.IsObjectElement).ToList();
        var childIndent = ChildIndent(container);

        if (index < objectChildren.Count)
        {
            // Before an existing child: "<new/>\n<indent>" goes between that child's leading whitespace and the child.
            var spacing = new XText("\n" + childIndent);
            objectChildren[index].AddBeforeSelf(child, spacing);
            return [child, spacing];
        }

        var lastElement = container.Elements().LastOrDefault();
        if (lastElement is not null)
        {
            // After the last element: "\n<indent><new/>", before whatever whitespace closed the container.
            var spacing = new XText("\n" + childIndent);
            lastElement.AddAfterSelf(spacing, child);
            return [spacing, child];
        }

        if (container.LastNode is XText { Value: var text } closing && string.IsNullOrWhiteSpace(text) && text.Contains('\n'))
        {
            // "<Grid>\n</Grid>": put the child before the existing closing whitespace.
            var spacing = new XText("\n" + childIndent);
            closing.AddBeforeSelf(spacing, child);
            return [spacing, child];
        }

        // Empty or self-closing container.
        var open = new XText("\n" + childIndent);
        var close = new XText("\n" + LineIndent(container));
        container.Add(open, child, close);
        return [open, child, close];
    }

    /// <summary>
    /// Re-lays out the whitespace inside <paramref name="element"/> for a new position in a document: one child per
    /// line, <paramref name="unit"/> deeper per level, starting from <paramref name="indent"/> (the element's own
    /// indentation). Elements with real text content are left alone.
    /// </summary>
    public static void Reindent(XElement element, string indent, string unit)
    {
        var hasText = element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value));
        if (hasText)
        {
            return;
        }

        foreach (var whitespace in element.Nodes().OfType<XText>().ToList())
        {
            whitespace.Remove();
        }

        var children = element.Elements().ToList();
        if (children.Count == 0)
        {
            return;
        }

        foreach (var child in children)
        {
            child.AddBeforeSelf(new XText("\n" + indent + unit));
            Reindent(child, indent + unit, unit);
        }

        children[^1].AddAfterSelf(new XText("\n" + indent));
    }

    /// <summary>Indentation of the line an element starts on (the whitespace after the last line break before it).</summary>
    public static string LineIndent(XElement element)
    {
        if (element.PreviousNode is XText { Value: var text } && string.IsNullOrWhiteSpace(text))
        {
            var newline = text.LastIndexOf('\n');
            return newline >= 0 ? text[(newline + 1)..] : "";
        }

        return "";
    }

    /// <summary>Indentation for a new child: like the existing children, or one level deeper than the container.</summary>
    public static string ChildIndent(XElement container)
    {
        var sibling = container.Elements().FirstOrDefault(e => e.PreviousNode is XText t && t.Value.Contains('\n'));
        return sibling is not null ? LineIndent(sibling) : LineIndent(container) + IndentUnit(container.Document);
    }

    /// <summary>The document's indent step, learned from the first nested element (default four spaces).</summary>
    public static string IndentUnit(XDocument? document)
    {
        if (document?.Root is null)
        {
            return DefaultIndentUnit;
        }

        foreach (var element in document.Root.Descendants())
        {
            if (element.PreviousNode is XText t && t.Value.Contains('\n') && element.Parent is { } parent)
            {
                var own = LineIndent(element);
                var parentIndent = parent == document.Root ? "" : LineIndent(parent);
                if (own.Length > parentIndent.Length && own.StartsWith(parentIndent, StringComparison.Ordinal))
                {
                    return own[parentIndent.Length..];
                }
            }
        }

        return DefaultIndentUnit;
    }
}
