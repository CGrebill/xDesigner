using System.Xml.Linq;
using XDesigner.Protocol;

namespace XDesigner.Core.Xaml;

/// <summary>
/// Builds the render copy of a XAML document: declares <c>xmlns:dsn</c> and gives every object element a
/// design id so rendered visuals map back to XAML elements (§5.4). The render host strips <c>x:Class</c>,
/// event attributes, and ids on elements that can't carry them.
/// </summary>
/// <remarks>
/// <see cref="Build(string)"/> numbers elements in document order (tests and one-off renders); the designer passes the
/// stable ids of its <c>XamlDocument</c> through <see cref="Build(XDocument, Func{XElement, int?}, IReadOnlyDictionary{int, IReadOnlyDictionary{string, string}})"/>.
/// </remarks>
public static class RenderCopyBuilder
{
    public static readonly XNamespace DesignIdNamespace = ProtocolInfo.DesignIdXmlNamespace;
    public static readonly XName DesignIdAttribute = DesignIdNamespace + ProtocolInfo.DesignIdAttributeName;

    /// <summary>Ids in document order (1, 2, 3, ...).</summary>
    public static string Build(string xaml)
    {
        var document = XDocument.Parse(xaml, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        var next = 0;
        return Build(document, _ => ++next);
    }

    /// <summary>
    /// Ids from <paramref name="idFor"/>; elements it returns null for get none. <paramref name="overrides"/> sets design-only
    /// attributes on elements of the copy by id (e.g. which tab of a TabControl to show); the document is untouched.
    /// </summary>
    public static string Build(XDocument source, Func<XElement, int?> idFor, IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>>? overrides = null)
    {
        var copy = new XDocument(source);
        var root = copy.Root ?? throw new ArgumentException("The XAML document has no root element.", nameof(source));
        root.SetAttributeValue(XNamespace.Xmlns + "dsn", ProtocolInfo.DesignIdXmlNamespace);

        // Walk source and copy in step so ids are computed from the original elements.
        foreach (var (original, clone) in source.Root!.DescendantsAndSelf().Zip(root.DescendantsAndSelf()))
        {
            if (IsObjectElement(original) && idFor(original) is { } id)
            {
                clone.SetAttributeValue(DesignIdAttribute, id);
                if (overrides?.GetValueOrDefault(id) is { } attributes)
                {
                    foreach (var (name, value) in attributes)
                    {
                        clone.SetAttributeValue(name, value);
                    }
                }
            }
        }

        return WithRootOnItsLine(source.Root!, root);
    }

    /// <summary>
    /// The root element's text, preceded by as many line breaks as there were lines before it in the file, so the
    /// loader's error line numbers match the file even when it starts with a declaration or comments (§8.5).
    /// </summary>
    public static string WithRootOnItsLine(XElement original, XElement root)
    {
        var line = original is System.Xml.IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 1;
        return new string('\n', Math.Max(0, line - 1)) + root.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// True for elements that create objects (<c>&lt;Button&gt;</c>), false for property elements
    /// (<c>&lt;Grid.RowDefinitions&gt;</c>) and XAML-language/design-namespace elements (<c>&lt;x:String&gt;</c>).
    /// </summary>
    public static bool IsObjectElement(XElement element) =>
        !element.Name.LocalName.Contains('.')
        && !Frameworks.FrameworkProfile.IsXamlLanguage(element.Name.Namespace)
        && !Frameworks.FrameworkProfile.IsDesign(element.Name.Namespace)
        && element.Name.NamespaceName != XamlNamespaces.MarkupCompatibility;

    /// <summary>An element's <c>x:Name</c> (2006 or 2009 XAML namespace) or <c>Name</c>, if set.</summary>
    public static string? NameOf(XElement element) =>
        (string?)element.Attribute(XName.Get("Name", Frameworks.FrameworkProfile.Xaml2006Namespace))
        ?? (string?)element.Attribute(XName.Get("Name", Frameworks.FrameworkProfile.Xaml2009Namespace))
        ?? (string?)element.Attribute("Name");
}
