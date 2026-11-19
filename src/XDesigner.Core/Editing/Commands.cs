using System.Xml.Linq;
using XDesigner.Core.Xaml;

namespace XDesigner.Core.Editing;

/// <summary>An edit that can be undone and redone (§6). Commands address elements by node id or instance.</summary>
public interface IUndoableCommand
{
    /// <summary>Shown in the Edit menu ("Undo Add Button").</summary>
    string Description { get; }

    void Execute(XamlDocument document);

    void Undo(XamlDocument document);

    /// <summary>Absorbs <paramref name="later"/> into this command (e.g. typing in one property); returns false if it can't.</summary>
    bool TryMerge(IUndoableCommand later) => false;
}

/// <summary>Sets, changes or removes (<c>null</c>) one attribute. Undo puts a removed attribute back in its original position.</summary>
public sealed class SetAttributeCommand(int nodeId, XName attribute, string? value, string? description = null) : IUndoableCommand
{
    private bool _captured;
    private string? _oldValue;
    private int _oldIndex = -1;

    public int NodeId { get; } = nodeId;

    public XName Attribute { get; } = attribute;

    public string? Value { get; private set; } = value;

    public string Description { get; } = description ?? $"Set {attribute.LocalName}";

    public void Execute(XamlDocument document)
    {
        var element = Resolve(document);
        if (!_captured)
        {
            var existing = element.Attribute(Attribute);
            _oldValue = existing?.Value;
            _oldIndex = existing is null ? -1 : element.Attributes().ToList().IndexOf(existing);
            _captured = true;
        }

        element.SetAttributeValue(Attribute, Value);
    }

    public void Undo(XamlDocument document)
    {
        var element = Resolve(document);
        if (_oldValue is null)
        {
            element.Attribute(Attribute)?.Remove();
        }
        else if (element.Attribute(Attribute) is not null)
        {
            element.SetAttributeValue(Attribute, _oldValue);
        }
        else
        {
            // Re-insert at its old position so the start tag serializes exactly as before.
            var attributes = element.Attributes().ToList();
            attributes.Insert(Math.Min(_oldIndex, attributes.Count), new XAttribute(Attribute, _oldValue));
            element.ReplaceAttributes(attributes);
        }
    }

    /// <summary>Consecutive edits of the same attribute on the same element become one undo step.</summary>
    public bool TryMerge(IUndoableCommand later)
    {
        if (later is SetAttributeCommand other && other.NodeId == NodeId && other.Attribute == Attribute)
        {
            Value = other.Value;
            return true;
        }

        return false;
    }

    private XElement Resolve(XamlDocument document) =>
        document.FindById(NodeId)?.Element ?? throw new InvalidOperationException($"Element #{NodeId} is no longer in the document.");
}

/// <summary>Inserts a new element (with matching indentation) into a container element.</summary>
/// <param name="containerId">The parent element's node id.</param>
/// <param name="index">Position among the container's object children; past the end appends.</param>
/// <param name="intoPropertyElement">Optional property element of the container to insert into (e.g. <c>Grid.Children</c>).</param>
public sealed class InsertElementCommand(int containerId, XElement element, int index, string description, XName? intoPropertyElement = null) : IUndoableCommand
{
    private IReadOnlyList<XNode> _added = [];

    public XElement Element { get; } = element;

    public string Description { get; } = description;

    public void Execute(XamlDocument document)
    {
        var parent = document.FindById(containerId)?.Element
            ?? throw new InvalidOperationException($"Element #{containerId} is no longer in the document.");
        var container = intoPropertyElement is { } name ? parent.Element(name) ?? parent : parent;
        _added = XamlFormatting.InsertChild(container, Element, index);
    }

    public void Undo(XamlDocument document)
    {
        foreach (var node in _added)
        {
            node.Remove();
        }
    }
}

/// <summary>Removes an element together with the whitespace in front of it; undo restores both exactly.</summary>
public sealed class RemoveElementCommand : IUndoableCommand
{
    private readonly int _nodeId;
    private readonly XName? _propertyElement;
    private readonly List<XNode> _removed = [];
    private XContainer? _parent;
    private List<XNode> _parentNodesBefore = [];

    public RemoveElementCommand(int nodeId, string? description = null)
    {
        _nodeId = nodeId;
        Description = description ?? "Delete";
    }

    /// <summary>Removes a property element of an element instead (<c>&lt;Button.Background&gt;</c>), e.g. to reset a property.</summary>
    public RemoveElementCommand(int ownerId, XName propertyElement, string description)
    {
        _nodeId = ownerId;
        _propertyElement = propertyElement;
        Description = description;
    }

    public string Description { get; }

    public void Execute(XamlDocument document)
    {
        var element = document.FindById(_nodeId)?.Element
            ?? throw new InvalidOperationException($"Element #{_nodeId} is no longer in the document.");
        if (_propertyElement is { } name)
        {
            element = element.Element(name) ?? throw new InvalidOperationException($"<{name.LocalName}> is no longer in the document.");
        }

        _parent = element.Parent ?? throw new InvalidOperationException("The root element can't be removed.");
        _parentNodesBefore = _parent.Nodes().ToList();
        _removed.Clear();

        if (element.PreviousNode is XText { Value: var text } leading && string.IsNullOrWhiteSpace(text))
        {
            _removed.Add(leading);
        }

        _removed.Add(element);
        foreach (var node in _removed)
        {
            node.Remove();
        }

        // If only whitespace is left, remove it too: an emptied "<Canvas>\n  </Canvas>" goes back to however the
        // tag was written originally (e.g. "<Canvas />") instead of keeping a blank line.
        if (_parent.Nodes().All(n => n is XText t && string.IsNullOrWhiteSpace(t.Value)))
        {
            _parent.RemoveNodes();
        }
    }

    /// <summary>Puts back exactly the parent's original nodes (same instances, same order).</summary>
    public void Undo(XamlDocument document)
    {
        _parent!.RemoveNodes();
        _parent.Add(_parentNodesBefore);
    }
}

/// <summary>
/// Replaces the text written inside an element (<c>&lt;Button&gt;OK&lt;/Button&gt;</c>) with <paramref name="text"/>,
/// or removes it (<c>null</c>). Child elements stay. Undo restores the element's exact original nodes.
/// </summary>
public sealed class SetTextContentCommand(int nodeId, string? text, string description) : IUndoableCommand
{
    private XElement? _element;
    private List<XNode> _before = [];

    public string Description { get; } = description;

    public void Execute(XamlDocument document)
    {
        _element = document.FindById(nodeId)?.Element
            ?? throw new InvalidOperationException($"Element #{nodeId} is no longer in the document.");
        _before = _element.Nodes().ToList();

        var texts = _element.Nodes().OfType<XText>().Where(t => !string.IsNullOrWhiteSpace(t.Value)).ToList();
        if (text is null)
        {
            texts.ForEach(t => t.Remove());
            if (_element.Nodes().All(n => n is XText t && string.IsNullOrWhiteSpace(t.Value)))
            {
                _element.RemoveNodes(); // "<Button>OK</Button>" becomes "<Button></Button>", like an emptied container
            }

            return;
        }

        if (texts.Count > 0)
        {
            texts[0].ReplaceWith(new XText(text)); // a new node, so undo puts the original (unchanged) one back
            texts.Skip(1).ToList().ForEach(t => t.Remove());
        }
        else
        {
            _element.AddFirst(new XText(text));
        }
    }

    public void Undo(XamlDocument document)
    {
        _element!.RemoveNodes();
        _element.Add(_before);
    }
}

/// <summary>Several commands as one undo step (a drop, a multi-select edit, a paste).</summary>
public sealed class CompositeCommand(string description, IReadOnlyList<IUndoableCommand> commands) : IUndoableCommand
{
    public string Description { get; } = description;

    public IReadOnlyList<IUndoableCommand> Commands { get; } = commands;

    /// <summary>
    /// The same attribute edits on the same elements merge (typing into a property with several controls selected,
    /// repeated nudges). Anything else stays a separate step.
    /// </summary>
    public bool TryMerge(IUndoableCommand later)
    {
        if (later is not CompositeCommand other || other.Commands.Count != Commands.Count)
        {
            return false;
        }

        for (var i = 0; i < Commands.Count; i++)
        {
            if (Commands[i] is not SetAttributeCommand a || other.Commands[i] is not SetAttributeCommand b
                || a.NodeId != b.NodeId || a.Attribute != b.Attribute)
            {
                return false;
            }
        }

        for (var i = 0; i < Commands.Count; i++)
        {
            Commands[i].TryMerge(other.Commands[i]);
        }

        return true;
    }

    public void Execute(XamlDocument document)
    {
        foreach (var command in Commands)
        {
            command.Execute(document);
        }
    }

    public void Undo(XamlDocument document)
    {
        for (var i = Commands.Count - 1; i >= 0; i--)
        {
            Commands[i].Undo(document);
        }
    }
}
