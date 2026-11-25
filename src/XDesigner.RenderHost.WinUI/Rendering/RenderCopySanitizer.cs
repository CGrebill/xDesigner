using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using XDesigner.Protocol;
using XDesigner.RenderHost.DesignTime;

namespace XDesigner.RenderHost.Rendering;

/// <summary>A render copy ready for <c>XamlReader.Load</c>, and what the sanitizer learned about the document on the way.</summary>
/// <param name="IsWindow">The document is a Window: <see cref="Xaml"/> holds its content in a stand-in Grid.</param>
/// <param name="DesignWidth">The root's <c>d:DesignWidth</c>, if it has one.</param>
internal sealed record SanitizedXaml(string Xaml, bool IsWindow, string? WindowTitle, double? DesignWidth, double? DesignHeight);

/// <summary>
/// Last transforms on a render copy before it is loaded, done here because only the host can resolve every element's real
/// type (including the user's custom controls). See docs/ARCHITECTURE.md §5.4 and §14.5.
/// <list type="bullet">
/// <item>a <c>Window</c> root becomes a Grid with the window's content (a Window is not an element and can't be rendered);</item>
/// <item><c>x:Class</c>, event attributes and the compiled-binding attributes (<c>{x:Bind}</c>, <c>x:Load</c>, <c>x:Phase</c>,
/// <c>x:DataType</c>, <c>x:DefaultBindMode</c>) are removed: runtime XAML has no code-behind to compile them against;</item>
/// <item>attributes and elements of ignorable namespaces (<c>d:</c>) are removed, after reading the design size;</item>
/// <item>design ids move to the host's own namespace, and off elements that aren't DependencyObjects.</item>
/// </list>
/// </summary>
internal sealed class RenderCopySanitizer(XamlTypeResolver resolver, ProjectResources? resources = null)
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace MarkupCompatibilityNs = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XNamespace PresentationNs = XamlTypeResolver.PresentationNamespace;
    private static readonly XName DesignIdAttribute = XName.Get(ProtocolInfo.DesignIdAttributeName, DesignId.XmlNamespace);
    private static readonly string[] CompiledOnlyDirectives = ["Class", "Load", "Phase", "DataType", "DefaultBindMode", "DeferLoadStrategy", "ClassModifier", "FieldModifier"];

    /// <summary>Returns the sanitized XAML, or the input unchanged if it isn't well-formed XML (the loader then reports the error).</summary>
    public SanitizedXaml Sanitize(string xaml)
    {
        XDocument document;
        try
        {
            // Line info, so the copy can be written back with every element on its line (LineKeepingXml, §8.5).
            document = XDocument.Parse(xaml, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (System.Xml.XmlException)
        {
            return new SanitizedXaml(xaml, false, null, null, null);
        }

        var root = document.Root!;
        var ignorable = IgnorableNamespaces(root);
        var designWidth = ReadDesignSize(root, ignorable, "DesignWidth");
        var designHeight = ReadDesignSize(root, ignorable, "DesignHeight");
        RemoveIgnorable(root, ignorable);
        MapDesignIds(document);

        // First, so the root's design id survives (a Window isn't a DependencyObject, the stand-in Grid is).
        var isWindow = false;
        string? title = null;
        if (root.Name.LocalName == "Window" && resolver.Resolve(root.Name.NamespaceName, root.Name.LocalName) is { } rootType
            && typeof(Window).IsAssignableFrom(rootType))
        {
            isWindow = true;
            // A bound title ({x:Bind …}) has no text to show (review L39).
            title = root.Attribute("Title")?.Value is { } t && !t.TrimStart().StartsWith('{') ? t : null;
            ReplaceWindowRoot(root);
        }

        foreach (var element in document.Descendants().ToList())
        {
            if (resources is not null)
            {
                ApplyProjectResources(element, resources);
            }

            foreach (var directive in CompiledOnlyDirectives)
            {
                element.Attribute(XamlNs + directive)?.Remove();
            }

            foreach (var attribute in element.Attributes().Where(IsCompiledBinding).ToList())
            {
                attribute.Remove();
            }

            if (element.Name.LocalName.Contains('.'))
            {
                continue; // property element such as <Grid.RowDefinitions>
            }

            if (element.Name.Namespace == XamlNs)
            {
                element.Attribute(DesignIdAttribute)?.Remove(); // x:String, x:Double...
                continue;
            }

            var type = resolver.Resolve(element.Name.NamespaceName, element.Name.LocalName);
            if (type is null)
            {
                continue; // unknown type: leave it, the loader will report it
            }

            if (!typeof(DependencyObject).IsAssignableFrom(type))
            {
                element.Attribute(DesignIdAttribute)?.Remove();
            }

            foreach (var attribute in element.Attributes().Where(a => IsEventAttribute(element, type, a)).ToList())
            {
                attribute.Remove();
            }
        }

        // Every element and attribute stays on its line, so error lines match the file (§8.5).
        return new SanitizedXaml(XDesigner.Protocol.Rendering.LineKeepingXml.Write(root), isWindow, title, designWidth, designHeight);
    }

    /// <summary>
    /// A Window is not a UIElement: render its content in a Grid that stands for the client area. The Grid keeps the root's
    /// namespace declarations and design id (so the root still maps to the whole client area); window-only attributes and
    /// property elements (<c>Title</c>, <c>&lt;Window.SystemBackdrop&gt;</c>) go.
    /// </summary>
    private static void ReplaceWindowRoot(XElement root)
    {
        foreach (var attribute in root.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name != DesignIdAttribute && a.Name.Namespace != XamlNs).ToList())
        {
            attribute.Remove();
        }

        foreach (var propertyElement in root.Elements().Where(e => e.Name.LocalName.StartsWith(root.Name.LocalName + ".", StringComparison.Ordinal)).ToList())
        {
            // <Window.Content> is the client area's content: it stays, unwrapped into the Grid (review L39).
            if (propertyElement.Name.LocalName == root.Name.LocalName + ".Content")
            {
                propertyElement.ReplaceWith(propertyElement.Nodes());
            }
            else
            {
                propertyElement.Remove();
            }
        }

        root.Name = PresentationNs + "Grid";
    }

    /// <summary>
    /// What the project's resource index would do (review M27): <c>x:Uid</c> strings become the attributes they set (an
    /// attribute written in the XAML stays), and <c>ms-appx:///</c> images point at a scale-qualified file when that's all there is.
    /// </summary>
    private static void ApplyProjectResources(XElement element, ProjectResources resources)
    {
        if (element.Attribute(XamlNs + "Uid") is { } uid)
        {
            var prefix = uid.Value + ".";
            foreach (var (key, text) in resources.Strings.Where(s => s.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                var property = key[prefix.Length..];
                if (property.Length > 0 && !property.Contains('.') && !property.Contains('[') && element.Attribute(property) is null)
                {
                    element.SetAttributeValue(property, text);
                }
            }

            uid.Remove(); // no lookups in the host's own index
        }

        foreach (var attribute in element.Attributes().Where(a => a.Value.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase)))
        {
            attribute.Value = resources.ResolveAppxUri(attribute.Value);
        }
    }

    private static HashSet<XNamespace> IgnorableNamespaces(XElement root)
    {
        var result = new HashSet<XNamespace>();
        if (root.Attribute(MarkupCompatibilityNs + "Ignorable")?.Value is { } prefixes)
        {
            foreach (var prefix in prefixes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (root.GetNamespaceOfPrefix(prefix) is { } ns)
                {
                    result.Add(ns);
                }
            }
        }

        return result;
    }

    private static double? ReadDesignSize(XElement root, HashSet<XNamespace> ignorable, string name)
    {
        var value = root.Attributes().FirstOrDefault(a => ignorable.Contains(a.Name.Namespace) && a.Name.LocalName == name)?.Value;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) && size > 0 && double.IsFinite(size) ? size : null;
    }

    private static void RemoveIgnorable(XElement root, HashSet<XNamespace> ignorable)
    {
        root.Attribute(MarkupCompatibilityNs + "Ignorable")?.Remove();
        if (ignorable.Count == 0)
        {
            return;
        }

        foreach (var element in root.DescendantsAndSelf().ToList())
        {
            if (ignorable.Contains(element.Name.Namespace) && element != root)
            {
                element.Remove();
                continue;
            }

            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration && ignorable.Contains(a.Name.Namespace)).ToList())
            {
                attribute.Remove();
            }
        }
    }

    /// <summary>The app marks design ids with a neutral namespace (§5.4); this host's DesignId lives in its own.</summary>
    private static void MapDesignIds(XDocument document)
    {
        XNamespace marker = ProtocolInfo.DesignIdXmlNamespace;
        XNamespace own = DesignId.XmlNamespace;
        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes().ToList())
            {
                if (attribute.IsNamespaceDeclaration && attribute.Value == marker.NamespaceName)
                {
                    attribute.Value = own.NamespaceName;
                }
                else if (attribute.Name.Namespace == marker)
                {
                    attribute.Remove();
                    element.SetAttributeValue(own + attribute.Name.LocalName, attribute.Value);
                }
            }
        }
    }

    /// <summary><c>{x:Bind …}</c>: compiled bindings need generated code that runtime XAML doesn't have.</summary>
    private static bool IsCompiledBinding(XAttribute attribute)
    {
        if (attribute.IsNamespaceDeclaration)
        {
            return false;
        }

        var value = attribute.Value.AsSpan().TrimStart();
        if (!value.StartsWith("{") || value.StartsWith("{}"))
        {
            return false;
        }

        var name = value[1..].TrimStart();
        var end = name.IndexOfAny(' ', '}', ',');
        var markup = (end < 0 ? name : name[..end]).ToString();
        var colon = markup.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && markup[(colon + 1)..] == "Bind" && attribute.Parent?.GetNamespaceOfPrefix(markup[..colon]) == XamlNs;
    }

    private bool IsEventAttribute(XElement element, Type elementType, XAttribute attribute)
    {
        if (attribute.IsNamespaceDeclaration)
        {
            return false;
        }

        var name = attribute.Name.LocalName;
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return attribute.Name.Namespace == XNamespace.None && IsEvent(elementType, name);
        }

        var ownerNamespace = attribute.Name.Namespace == XNamespace.None ? element.GetDefaultNamespace().NamespaceName : attribute.Name.NamespaceName;
        var owner = resolver.Resolve(ownerNamespace, name[..dot]);
        return owner is not null && IsEvent(owner, name[(dot + 1)..]);
    }

    private static bool IsEvent(Type type, string name) =>
        type.GetEvent(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static) is not null;
}
