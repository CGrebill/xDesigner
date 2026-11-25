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
    private readonly XamlDocument _document = document;
    private readonly UndoManager _undo = undo;

    /// <summary>The document's XAML language namespace (<c>x:</c>; 2009 for MAUI).</summary>
    /// <summary>The <c>x:</c> namespace this file uses (2006 or 2009, §5.3).</summary>
    private XNamespace XamlNs => _document.XamlNamespace;

    /// <summary>
    /// Adds a new control as planned by <see cref="DropPlanner"/>: generated <c>x:Name</c>, placement attributes,
    /// toolbox defaults, and any namespace declaration the root needs. Returns the new element's node id.
    /// </summary>
    public int AddControl(ToolboxItem item, DropPlan plan)
    {
        var type = item.Type;
        var commands = new List<IUndoableCommand>();
        var root = _document.Xml.Root!;

        if (root.PrefixOf(XamlNs) is null)
        {
            commands.Add(new SetAttributeCommand(_document.Root.Id, XNamespace.Xmlns + _document.Xml.Root!.FreePrefix("x"), XamlNs.NamespaceName));
        }

        var elementNamespace = ElementNamespace(root, type.XmlNamespace, type.ClrNamespace, out var declaration);
        if (declaration is { } decl)
        {
            commands.Add(new SetAttributeCommand(_document.Root.Id, XNamespace.Xmlns + decl.Prefix, decl.Namespace));
        }

        var name = NameGenerator.Next(_document, type.Name);
        var element = new XElement(elementNamespace + type.Name);

        // Attribute order (§5.5): x:Name, attached layout, size, alignment/margin, then the rest. The plan's placement is
        // in canonical names (§7.1); toolbox defaults are already in the framework's own.
        var vocabulary = _document.Profile.Layout;
        string[] sizeKeys = [vocabulary.Width, vocabulary.Height];
        element.SetAttributeValue(XamlNs + "Name", name);
        var layout = plan.Attributes.Select(a => new KeyValuePair<XName, string?>(a.Key, a.Value)).ToList();
        vocabulary.Apply(element, layout.Where(a => a.Key.LocalName.Contains('.')));

        var defaults = item.Attributes.ToDictionary(a => a.Key, a => a.Value.Replace("{name}", name, StringComparison.Ordinal));
        if (plan.IsPositioned)
        {
            foreach (var (key, value) in item.SizeWhenPositioned)
            {
                defaults.TryAdd(key, value);
            }
        }

        foreach (var key in sizeKeys.Where(defaults.ContainsKey))
        {
            element.SetAttributeValue(key, defaults[key]);
        }

        vocabulary.Apply(element, layout.Where(a => !a.Key.LocalName.Contains('.')));

        foreach (var (key, value) in defaults.Where(d => !sizeKeys.Contains(d.Key)))
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
        if (xmlNamespace == root.DefaultNamespaceOf().NamespaceName)
        {
            return xmlNamespace; // the framework's own controls: no prefix
        }

        foreach (var attribute in root.Attributes().Where(a => a.IsNamespaceDeclaration))
        {
            // The same CLR namespace in the same assembly: MAUI's clr-namespace names one (review L3).
            if (attribute.Value == xmlNamespace
                || (ControlCatalog.ClrNamespaceOf(attribute.Value) == clrNamespace
                    && (AssemblyOf(xmlNamespace) is not { } wanted || AssemblyOf(attribute.Value) == wanted)))
            {
                return attribute.Value;
            }
        }

        var stem = root.FreePrefix("local") == "local" ? "local" : clrNamespace.Split('.')[^1].ToLowerInvariant();
        declaration = (root.FreePrefix(stem), xmlNamespace);
        return xmlNamespace;
    }

    /// <summary>The <c>assembly=</c> part of a <c>clr-namespace:</c> XML namespace; null for <c>using:</c> and URLs.</summary>
    private static string? AssemblyOf(string xmlNamespace) =>
        xmlNamespace.Split(';').Skip(1).FirstOrDefault(p => p.StartsWith("assembly=", StringComparison.Ordinal))?["assembly=".Length..];
}
