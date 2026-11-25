namespace XDesigner.Core.Xaml;

/// <summary>
/// XML namespaces every framework's XAML shares. The frameworks' own (controls, <c>x:</c>, <c>d:</c>) are
/// <see cref="Frameworks.FrameworkProfile"/>'s. See docs/ARCHITECTURE.md §5.3.
/// </summary>
public static class XamlNamespaces
{
    /// <summary>Markup compatibility namespace (<c>mc:Ignorable</c>).</summary>
    public const string MarkupCompatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";
}
