namespace XDesigner.Core.Xaml;

/// <summary>
/// Well-known XML namespaces used in Avalonia XAML documents.
/// See docs/ARCHITECTURE.md §5.3.
/// </summary>
public static class XamlNamespaces
{
    /// <summary>Default namespace for built-in Avalonia controls.</summary>
    public const string Avalonia = "https://github.com/avaloniaui";

    /// <summary>XAML language namespace (<c>x:Name</c>, <c>x:Class</c>, ...).</summary>
    public const string Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Design-time namespace (<c>d:DesignWidth</c>, ...).</summary>
    public const string Design = "http://schemas.microsoft.com/expression/blend/2008";

    /// <summary>Markup compatibility namespace (<c>mc:Ignorable</c>).</summary>
    public const string MarkupCompatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";
}
