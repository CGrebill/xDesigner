using System.Xml.Linq;
using XDesigner.Core.CodeGen;
using XDesigner.Core.Metadata;
using XDesigner.Core.Naming;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;
using Microsoft.CodeAnalysis.CSharp;

namespace XDesigner.Core.Editing;

/// <summary>What connecting an event did, for the confirmation (§10.1 step 7, D9) and the Events log.</summary>
/// <param name="Handler">Where the method is; null when it failed.</param>
/// <param name="ControlName">The control's name (generated if it had none), or the class name for the root.</param>
/// <param name="CreatedCodeBehind">The <c>.axaml.cs</c> file didn't exist and was created.</param>
public sealed record EventHandlerOutcome(
    bool Success,
    string Message,
    string EventName,
    string TypeName,
    string? ControlName,
    HandlerResult? Handler,
    bool CreatedCodeBehind = false)
{
    public static EventHandlerOutcome Failed(string message, string eventName, string typeName) => new(false, message, eventName, typeName, null, null);
}

/// <summary>Event handlers (§10): connect an event to a code-behind method (creating it if needed), or disconnect it.</summary>
public sealed partial class DesignerActions
{
    /// <summary>
    /// Connects <paramref name="evt"/> on the elements to <paramref name="methodName"/> (default <c>{name}_{Event}</c>, which
    /// needs a single element), adding the method to <c>{xamlPath}.cs</c> when it isn't there. The code-behind is written
    /// first and immediately (D11); only then the XAML changes, as one undo step that also names an unnamed control.
    /// Undo removes the XAML attribute only: the method stays (D10).
    /// </summary>
    public EventHandlerOutcome ConnectHandler(IReadOnlyList<int> ids, EventInfoDto evt, string? methodName, string xamlPath, ControlCatalog catalog)
    {
        var nodes = ids.Select(_document.FindById).OfType<ElementNode>().ToList();
        var typeName = nodes.Count > 0 ? nodes[0].TypeName : "";
        if (nodes.Count == 0)
        {
            return EventHandlerOutcome.Failed("The control is no longer in the document.", evt.Name, typeName);
        }

        if (methodName is null && nodes.Count > 1)
        {
            return EventHandlerOutcome.Failed("Type a method name to share one handler between several controls.", evt.Name, typeName);
        }

        var root = _document.Root;
        if ((string?)root.Element.Attribute(XamlNs + "Class") is not { Length: > 0 } className)
        {
            return EventHandlerOutcome.Failed(
                "This file has no x:Class, so there is no code-behind class to add the handler to.", evt.Name, typeName);
        }

        // The control's name: the class name for the root, else its x:Name (generated if missing, set below).
        var node = nodes[0];
        string? newName = null;
        var controlName = node.Id == root.Id ? className[(className.LastIndexOf('.') + 1)..] : node.Name;
        if (controlName is null)
        {
            controlName = newName = NameGenerator.Next(_document, node.TypeName);
        }

        var method = (methodName ?? $"{controlName}_{evt.Name}").Trim();
        if (!SyntaxFacts.IsValidIdentifier(method) || SyntaxFacts.GetKeywordKind(method) != SyntaxKind.None)
        {
            return EventHandlerOutcome.Failed($"'{method}' can't be a C# method name.", evt.Name, typeName);
        }

        var codeBehind = CodeBehindWriter.CodeBehindPathFor(xamlPath);
        var created = false;
        HandlerResult handler;
        try
        {
            if (!File.Exists(codeBehind))
            {
                var rootType = catalog.Resolve(root);
                var isAvalonia = rootType?.XmlNamespace == ControlTypeInfo.AvaloniaXmlNamespace;
                CodeBehindWriter.CreateCodeBehind(codeBehind, className, isAvalonia ? root.TypeName : rootType?.FullName ?? root.TypeName,
                    isAvalonia ? rootType!.ClrNamespace : null, _document.NewLine);
                created = true;
            }

            handler = CodeBehindWriter.EnsureHandler(codeBehind, className, method, HandlerSignature.From(evt));
        }
        catch (CodeBehindException ex)
        {
            return EventHandlerOutcome.Failed(ex.Message, evt.Name, typeName);
        }

        var commands = new List<IUndoableCommand>();
        if (newName is not null)
        {
            if (_document.Xml.Root!.GetPrefixOfNamespace(XamlNs) is null)
            {
                commands.Add(new SetAttributeCommand(root.Id, XNamespace.Xmlns + "x", XamlNamespaces.Xaml));
            }

            commands.Add(new PrependAttributeCommand(node.Id, XamlNs + "Name", newName));
        }

        commands.AddRange(nodes.Where(n => (string?)n.Element.Attribute(evt.Name) != handler.MethodName)
            .Select(n => (IUndoableCommand)new SetAttributeCommand(n.Id, evt.Name, handler.MethodName)));
        if (commands.Count > 0)
        {
            _undo.Execute(new CompositeCommand($"Connect {evt.Name} to {handler.MethodName}", commands));
        }

        var who = nodes.Count == 1 ? $"{typeName}.{evt.Name} ({controlName})" : $"{evt.Name} on {nodes.Count} controls";
        var file = Path.GetFileName(handler.FilePath);
        var message = handler.Created
            ? $"Created handler {handler.MethodName} for {who} in {file}, line {handler.Line}."
            : $"Connected {who} to the existing method {handler.MethodName} in {file}, line {handler.Line}.";
        if (created)
        {
            message += $" ({file} didn't exist, so it was created.)";
        }

        return new EventHandlerOutcome(true, message, evt.Name, typeName, nodes.Count == 1 ? controlName : null, handler, created);
    }

    /// <summary>Removes the event attribute from the elements (one undo step). The C# method stays (D10). False if none had it.</summary>
    public bool DisconnectHandler(IReadOnlyList<int> ids, string eventName)
    {
        var commands = ids.Select(_document.FindById).OfType<ElementNode>()
            .Where(n => n.Element.Attribute(eventName) is not null)
            .Select(n => (IUndoableCommand)new SetAttributeCommand(n.Id, eventName, null))
            .ToList();
        if (commands.Count == 0)
        {
            return false;
        }

        _undo.Execute(new CompositeCommand($"Disconnect {eventName}", commands));
        return true;
    }
}
