using System.Xml.Linq;

namespace XDesigner.Core.Xaml;

/// <summary>
/// One object element (<c>&lt;Button&gt;</c>, not a property element like <c>&lt;Grid.RowDefinitions&gt;</c>)
/// with an id that stays the same for the whole session, including across undo/redo (§5.1).
/// The id is what the render host reports back as <c>DesignId</c>.
/// </summary>
public sealed class ElementNode
{
    /// <summary>Property elements whose contents are the element's visible children (shown in the outline).</summary>
    private static readonly string[] ContentPropertySuffixes = [".Content", ".Child", ".Children", ".Items"];

    internal ElementNode(XamlDocument document, XElement element, int id)
    {
        Document = document;
        Element = element;
        Id = id;
    }

    public int Id { get; }

    public XamlDocument Document { get; }

    public XElement Element { get; }

    /// <summary>Type name as written, without prefix (<c>Button</c>, <c>StarRating</c>).</summary>
    public string TypeName => Element.Name.LocalName;

    /// <summary>XML namespace of the type (<c>https://github.com/avaloniaui</c>, <c>using:My.Controls</c>).</summary>
    public string XmlNamespace => Element.Name.NamespaceName;

    /// <summary><c>x:Name</c> or <c>Name</c>, if set.</summary>
    public string? Name =>
        (string?)Element.Attribute(XName.Get("Name", XamlNamespaces.Xaml)) ?? (string?)Element.Attribute("Name");

    /// <summary>How the element is labelled in the outline and on the surface: <c>button1</c> or <c>[Button]</c>.</summary>
    public string DisplayName => Name ?? $"[{TypeName}]";

    /// <summary>Nearest object-element ancestor (property elements in between are skipped).</summary>
    public ElementNode? Parent
    {
        get
        {
            for (var e = Element.Parent; e is not null; e = e.Parent)
            {
                if (RenderCopyBuilder.IsObjectElement(e))
                {
                    return Document.NodeFor(e);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Children shown in the outline: direct object elements plus those inside content property elements
    /// (<c>&lt;Button.Content&gt;</c>, <c>&lt;Panel.Children&gt;</c>...). Resources, styles and
    /// definitions (<c>&lt;Grid.RowDefinitions&gt;</c>) are left out.
    /// </summary>
    public IReadOnlyList<ElementNode> Children
    {
        get
        {
            var children = new List<ElementNode>();
            foreach (var child in Element.Elements())
            {
                if (RenderCopyBuilder.IsObjectElement(child))
                {
                    children.Add(Document.NodeFor(child));
                }
                else if (IsContentPropertyElement(child))
                {
                    children.AddRange(child.Elements().Where(RenderCopyBuilder.IsObjectElement).Select(Document.NodeFor));
                }
            }

            return children;
        }
    }

    /// <summary>This node and its ancestors, nearest first.</summary>
    public IEnumerable<ElementNode> AncestorsAndSelf()
    {
        for (var node = this; node is not null; node = node.Parent)
        {
            yield return node;
        }
    }

    public override string ToString() => $"{DisplayName} (#{Id})";

    private static bool IsContentPropertyElement(XElement element) =>
        ContentPropertySuffixes.Any(s => element.Name.LocalName.EndsWith(s, StringComparison.Ordinal));
}
