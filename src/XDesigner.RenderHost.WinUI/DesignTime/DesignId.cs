using Microsoft.UI.Xaml;

namespace XDesigner.RenderHost.DesignTime;

/// <summary>
/// Attached property the designer injects into the render copy of a document (<c>dsn:DesignId.Value="17"</c>) so
/// rendered elements can be matched back to XAML elements. See docs/ARCHITECTURE.md §5.4 and §14.2.
/// </summary>
/// <remarks>
/// Not a DependencyObject: the XAML compiler would then take its inherited GetValue/SetValue for the attached property's
/// accessors and report "Unknown attachable member". TypeInfoAnchor.xaml makes the compiler generate its type information.
/// </remarks>
public sealed class DesignId
{
    /// <summary>Where this property lives in XAML. The sanitizer maps the app's neutral design-id namespace to it.</summary>
    public const string XmlNamespace = "using:XDesigner.RenderHost.DesignTime";

    /// <summary>0 means "not a designed element" (e.g. a template part).</summary>
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.RegisterAttached("Value", typeof(int), typeof(DesignId), new PropertyMetadata(0));

    public static int GetValue(DependencyObject element) => (int)element.GetValue(ValueProperty);

    public static void SetValue(DependencyObject element, int value) => element.SetValue(ValueProperty, value);
}
