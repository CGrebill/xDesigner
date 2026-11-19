using System.Xml.Linq;
using XDesigner.Core.Layout;
using XDesigner.Core.Metadata;
using XDesigner.Core.Naming;
using XDesigner.Core.Toolbox;
using XDesigner.Core.Xaml;

namespace XDesigner.Core.Editing;

/// <summary>
/// User-level edits, each built as one undoable command (§6). The app calls these; they never touch the
/// UI. Add and delete here; move/resize/nudge in DesignerActions.Move.cs; copy/paste in DesignerActions.Clipboard.cs.
/// </summary>
public sealed partial class DesignerActions(XamlDocument document, UndoManager undo)
{
    private static readonly string[] SizeKeys = ["Width", "Height"];

    private readonly XamlDocument _document = document;
    private readonly UndoManager _undo = undo;

    /// <summary>The document's XAML language namespace (<c>x:</c>; 2009 for MAUI).</summary>
    private XNamespace XamlNs => _document.Profile.XamlNamespace;

    /// <summary>
    /// Adds a new control as planned by <see cref="DropPlanner"/>: generated <c>x:Name</c>, placement attributes,
    /// toolbox defaults, and any namespace declaration the root needs. Returns the new element's node id.
    /// </summary>
    public int AddControl(ToolboxItem item, DropPlan plan)
    {
        var type = item.Type;
        var commands = new List<IUndoableCommand>();
        var root = _document.Xml.Root!;

        if (root.GetPrefixOfNamespace(XamlNs) is null)
        {
            commands.Add(new SetAttributeCommand(_document.Root.Id, XNamespace.Xmlns + "x", XamlNs.NamespaceName));
        }

        var elementNamespace = ElementNamespace(root, type.XmlNamespace, type.ClrNamespace, out var declaration);
        if (declaration is { } decl)
        {
            commands.Add(new SetAttributeCommand(_document.Root.Id, XNamespace.Xmlns + decl.Prefix, decl.Namespace));
        }

        var name = NameGenerator.Next(_document, type.Name);
        var element = new XElement(elementNamespace + type.Name);

        // Attribute order (§5.5): x:Name, attached layout, size, alignment/margin, then the rest.
        element.SetAttributeValue(XamlNs + "Name", name);
        var layout = plan.Attributes.ToList();
        foreach (var (key, value) in layout.Where(a => a.Key.LocalName.Contains('.')))
        {
            element.SetAttributeValue(key, value);
        }

        var defaults = item.Attributes.ToDictionary(a => a.Key, a => a.Value.Replace("{name}", name, StringComparison.Ordinal));
        if (plan.IsPositioned)
        {
            foreach (var (key, value) in item.SizeWhenPositioned)
            {
                defaults.TryAdd(key, value);
            }
        }

        foreach (var key in SizeKeys.Where(defaults.ContainsKey))
        {
            element.SetAttributeValue(key, defaults[key]);
        }

        foreach (var (key, value) in layout.Where(a => !a.Key.LocalName.Contains('.')))
        {
            element.SetAttributeValue(key, value);
        }

        foreach (var (key, value) in defaults.Where(d => d.Key is not ("Width" or "Height")))
        {
            element.SetAttributeValue(key, value);
        }

        commands.Add(new InsertElementCommand(plan.ContainerId, element, plan.Index, $"Add {type.Name}"));
        _undo.Execute(new CompositeCommand($"Add {type.Name}", commands));
        return _document.NodeFor(element).Id;
    }

    /// <summary>Deletes the given elements (never the root) as one undo step. Returns how many were deleted.</summary>
    public int Delete(IEnumerable<int> ids)
    {
        var nodes = ids.Select(_document.FindById).OfType<ElementNode>().Where(n => n.Parent is not null).ToList();

        // Deleting a parent deletes its children; don't also delete them separately.
        var set = nodes.Select(n => n.Id).ToHashSet();
        nodes = nodes.Where(n => !n.AncestorsAndSelf().Skip(1).Any(a => set.Contains(a.Id))).ToList();
        if (nodes.Count == 0)
        {
            return 0;
        }

        var description = nodes.Count == 1 ? $"Delete {nodes[0].DisplayName}" : $"Delete {nodes.Count} controls";
        _undo.Execute(new CompositeCommand(description, nodes.Select(n => (IUndoableCommand)new RemoveElementCommand(n.Id)).ToList()));
        return nodes.Count;
    }

    /// <summary>
    /// The XML namespace to create an element in, reusing an existing declaration on the root when one already
    /// refers to the same CLR namespace (<c>using:</c> or <c>clr-namespace:</c>), otherwise proposing a new one.
    /// </summary>
    public static XNamespace ElementNamespace(XElement root, string xmlNamespace, string clrNamespace, out (string Prefix, string Namespace)? declaration)
    {
        declaration = null;
        if (xmlNamespace == root.GetDefaultNamespace().NamespaceName)
        {
            return xmlNamespace; // the framework's own controls: no prefix
        }

        foreach (var attribute in root.Attributes().Where(a => a.IsNamespaceDeclaration))
        {
            if (attribute.Value == xmlNamespace || ControlCatalog.ClrNamespaceOf(attribute.Value) == clrNamespace)
            {
                return attribute.Value;
            }
        }

        var used = root.Attributes().Where(a => a.IsNamespaceDeclaration).Select(a => a.Name.LocalName).ToHashSet();
        var stem = used.Contains("local") ? clrNamespace.Split('.')[^1].ToLowerInvariant() : "local";
        var prefix = stem;
        for (var n = 2; used.Contains(prefix); n++)
        {
            prefix = stem + n;
        }

        declaration = (prefix, xmlNamespace);
        return xmlNamespace;
    }
}
