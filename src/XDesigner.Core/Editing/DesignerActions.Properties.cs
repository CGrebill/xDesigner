using System.Xml.Linq;
using XDesigner.Core.Metadata;
using XDesigner.Core.Naming;
using XDesigner.Core.Properties;
using XDesigner.Core.Xaml;

namespace XDesigner.Core.Editing;

/// <summary>Properties window edits (§9.2): set, reset and rename, each one undo step even for several elements.</summary>
public sealed partial class DesignerActions
{
    /// <summary>
    /// Sets <paramref name="key"/> to <paramref name="value"/> (already validated, see <see cref="XamlValueSerializer.TryNormalize"/>)
    /// on every element, or resets it when <paramref name="value"/> is null: the attribute, a property element and
    /// text content written for it are all removed. Text written inside an element is edited in place. Returns false
    /// when nothing changed.
    /// </summary>
    public bool SetProperty(IReadOnlyCollection<int> ids, string key, string? value, ControlCatalog catalog)
    {
        var commands = new List<IUndoableCommand>();
        var nodes = ids.Select(_document.FindById).OfType<ElementNode>().ToList();
        foreach (var node in nodes)
        {
            var element = node.Element;
            var type = catalog.Resolve(node);
            var isTextContentKey = key == PropertyGridBuilder.TextContentKey(type);
            var attribute = element.Attribute(key);
            var propertyElement = PropertyGridBuilder.PropertyElementOf(element, key);
            var hasText = isTextContentKey && PropertyGridBuilder.TextContentOf(element) is not null;

            if (value is null)
            {
                if (attribute is not null)
                {
                    commands.Add(new SetAttributeCommand(node.Id, key, null, $"Reset {key}"));
                }

                if (propertyElement is not null)
                {
                    commands.Add(new RemoveElementCommand(node.Id, propertyElement.Name, $"Reset {key}"));
                }

                if (hasText)
                {
                    commands.Add(new SetTextContentCommand(node.Id, null, $"Reset {key}"));
                }

                continue;
            }

            if (hasText && attribute is null)
            {
                if (PropertyGridBuilder.TextContentOf(element) != value)
                {
                    commands.Add(new SetTextContentCommand(node.Id, value, $"Set {key}"));
                }

                continue;
            }

            if (propertyElement is not null)
            {
                commands.Add(new RemoveElementCommand(node.Id, propertyElement.Name, $"Set {key}"));
            }

            var text = XamlValueSerializer.ToAttribute(value);
            if (attribute?.Value != text)
            {
                commands.Add(new SetAttributeCommand(node.Id, key, text));
            }
        }

        if (commands.Count == 0)
        {
            return false;
        }

        var verb = value is null ? "Reset" : "Set";
        var description = nodes.Count == 1 ? $"{verb} {key}" : $"{verb} {key} on {nodes.Count} controls";

        // A single attribute edit stays a SetAttributeCommand so that typing in one property merges into one step.
        _undo.Execute(commands is [SetAttributeCommand single] && value is not null
            ? new SetAttributeCommand(single.NodeId, single.Attribute, single.Value, description)
            : new CompositeCommand(description, commands));
        return true;
    }

    /// <summary>
    /// Renames an element (<c>x:Name</c>, or <c>Name</c> if that's what the file uses); empty removes the name.
    /// Event handlers and C# code that use the old name are left alone (D10). Returns why not when the name can't be used.
    /// </summary>
    public string? Rename(int id, string newName)
    {
        if (_document.FindById(id) is not { } node)
        {
            return "The control is no longer in the document.";
        }

        var name = newName.Trim();
        var old = node.Name;
        if (name == (old ?? ""))
        {
            return null;
        }

        var xName = XamlNs + "Name";
        var existing = node.Element.Attribute(xName) is not null || node.Element.Attribute("Name") is null ? xName : (XName)"Name";
        if (name.Length == 0)
        {
            _undo.Execute(new SetAttributeCommand(id, existing, null, $"Remove name {old}"));
            return null;
        }

        if (!NameGenerator.IsValid(_document, name, out var problem, id))
        {
            return problem;
        }

        var commands = new List<IUndoableCommand>();
        if (existing == xName && _document.Xml.Root!.GetPrefixOfNamespace(XamlNs) is null)
        {
            commands.Add(new SetAttributeCommand(_document.Root.Id, XNamespace.Xmlns + "x", XamlNamespaces.Xaml));
        }

        if (old is null && existing == xName)
        {
            // A new x:Name goes first in the tag, like the designer writes it for new controls (§5.5).
            commands.Add(new PrependAttributeCommand(id, xName, name));
        }
        else
        {
            commands.Add(new SetAttributeCommand(id, existing, name));
        }

        var description = old is null ? $"Name {node.TypeName} {name}" : $"Rename {old} to {name}";
        _undo.Execute(new CompositeCommand(description, commands));
        return null;
    }
}

/// <summary>Adds a new attribute at the start of the tag (undo removes it).</summary>
public sealed class PrependAttributeCommand(int nodeId, XName attribute, string value) : IUndoableCommand
{
    public string Description => $"Set {attribute.LocalName}";

    public void Execute(XamlDocument document)
    {
        var element = document.FindById(nodeId)?.Element
            ?? throw new InvalidOperationException($"Element #{nodeId} is no longer in the document.");
        var attributes = element.Attributes().ToList();
        var firstNonNamespace = attributes.FindIndex(a => !a.IsNamespaceDeclaration && a.Name.Namespace != XamlNamespaces.Xaml);
        attributes.Insert(firstNonNamespace < 0 ? attributes.Count : firstNonNamespace, new XAttribute(attribute, value));
        element.ReplaceAttributes(attributes);
    }

    public void Undo(XamlDocument document) => document.FindById(nodeId)?.Element.Attribute(attribute)?.Remove();
}
