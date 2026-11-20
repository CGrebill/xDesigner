namespace XDesigner.RenderHost.DesignTime;

/// <summary>
/// Attached property the designer injects into the render copy of a document (<c>dsn:DesignId.Value="17"</c>) so rendered
/// elements can be matched back to XAML elements. See docs/ARCHITECTURE.md §5.4 and §14.6.
/// </summary>
public static class DesignId
{
    /// <summary>Where this property lives in XAML; the sanitizer maps the app's neutral design-id namespace to it.</summary>
    public static readonly string XmlNamespace =
        $"clr-namespace:{typeof(DesignId).Namespace};assembly={typeof(DesignId).Assembly.GetName().Name}";

    /// <summary>0 means "not a designed element".</summary>
    public static readonly BindableProperty ValueProperty =
        BindableProperty.CreateAttached("Value", typeof(int), typeof(DesignId), 0);

    public static int GetValue(BindableObject element) => (int)element.GetValue(ValueProperty);

    public static void SetValue(BindableObject element, int value) => element.SetValue(ValueProperty, value);
}
