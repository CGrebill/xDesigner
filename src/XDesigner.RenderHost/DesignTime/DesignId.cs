using Avalonia;

namespace XDesigner.RenderHost.DesignTime;

/// <summary>
/// Attached property the designer injects into the render copy of a document
/// (<c>dsn:DesignId.Value="17"</c>) so rendered visuals can be matched back to XAML elements.
/// See docs/ARCHITECTURE.md §5.4 and §14.2.
/// </summary>
public sealed class DesignId
{
    /// <summary>XML namespace the app declares as <c>xmlns:dsn</c> in the render copy.</summary>
    public const string XmlNamespace = Protocol.ProtocolInfo.DesignIdXmlNamespace;

    /// <summary>0 means "not a designed element" (e.g. a template part).</summary>
    public static readonly AttachedProperty<int> ValueProperty =
        AvaloniaProperty.RegisterAttached<DesignId, AvaloniaObject, int>("Value");

    private DesignId()
    {
    }

    public static int GetValue(AvaloniaObject element) => element.GetValue(ValueProperty);

    public static void SetValue(AvaloniaObject element, int value) => element.SetValue(ValueProperty, value);
}
