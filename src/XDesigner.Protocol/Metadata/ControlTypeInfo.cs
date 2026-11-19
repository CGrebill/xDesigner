using MessagePack;

namespace XDesigner.Protocol.Metadata;

/// <summary>Where a control's children go (§5.2).</summary>
public enum ContentModel
{
    /// <summary>Not a container (TextBlock, Image, a user's own UserControl).</summary>
    None,

    /// <summary>One child, as content (ContentControl, Button, Window, ScrollViewer).</summary>
    SingleContent,

    /// <summary>One child (Border, Decorator, Viewbox).</summary>
    SingleChild,

    /// <summary>Any number of children (panels).</summary>
    Children,

    /// <summary>Items (ListBox, ComboBox, TabControl, Menu, TreeView).</summary>
    Items,

    /// <summary>Text runs (TextBlock); not a drop target.</summary>
    Inlines,
}

/// <summary>
/// What the designer needs to know about one control type: how to write it in XAML, whether the toolbox
/// can create it, and where its children go (§4, §14.3). Properties and events are fetched per type when needed (GetPropertiesAsync, GetEventsAsync).
/// </summary>
/// <param name="XmlNamespace">
/// Namespace to write the element in: <c>https://github.com/avaloniaui</c> for types mapped there by
/// <c>XmlnsDefinition</c> (built-in controls and many packages), otherwise <c>using:{ClrNamespace}</c>.
/// </param>
/// <param name="IsBuiltIn">From Avalonia's own assemblies (<c>Avalonia</c>, <c>Avalonia.*</c>).</param>
/// <param name="IsCreatable">Public, non-abstract, non-generic, not nested, with a public parameterless constructor.</param>
/// <param name="ContentPropertyName">The <c>[Content]</c> property, if any (<c>Content</c>, <c>Child</c>, <c>Children</c>, <c>Items</c>).</param>
/// <param name="DefaultEventName">The event double-clicking the control creates a handler for (§10.1).</param>
/// <param name="BaseTypes">Full names of base classes, nearest first, up to <c>Control</c>.</param>
/// <param name="EventNames">Names of the events that can be handled from XAML (routed and CLR), sorted.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record ControlTypeInfo(
    string FullName,
    string Name,
    string XmlNamespace,
    string ClrNamespace,
    string AssemblyName,
    bool IsBuiltIn,
    bool IsCreatable,
    ContentModel Content,
    string? ContentPropertyName,
    string? DefaultEventName,
    string[] BaseTypes,
    string[]? EventNames = null)
{
    public const string AvaloniaXmlNamespace = "https://github.com/avaloniaui";

    public bool DerivesFrom(string fullName) => FullName == fullName || BaseTypes.Contains(fullName);

    public bool HasEvent(string name) => EventNames?.Contains(name) == true;
}
