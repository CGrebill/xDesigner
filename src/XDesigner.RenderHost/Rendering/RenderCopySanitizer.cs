using System.Reflection;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Interactivity;
using XDesigner.RenderHost.DesignTime;

namespace XDesigner.RenderHost.Rendering;

/// <summary>
/// Last transforms on a render copy before it is loaded, done here because only the host can
/// resolve every element's real type (including the user's custom controls):
/// <list type="bullet">
/// <item>remove event attributes (<c>Click="button1_Click"</c>, attached <c>Button.Click="..."</c>):
/// a handler the designer just created isn't compiled yet, and the runtime loader rejects unknown handlers;</item>
/// <item>remove <c>x:Class</c> (the root must be built from the edited XAML, not the compiled class);</item>
/// <item>remove <c>dsn:DesignId.Value</c> from elements that aren't <see cref="AvaloniaObject"/>s
/// (styles, setters, resource dictionaries), where the attached property can't be set.</item>
/// </list>
/// See docs/ARCHITECTURE.md §5.4 and §14.2.
/// </summary>
internal sealed class RenderCopySanitizer(XamlTypeResolver resolver)
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XName DesignIdAttribute = XName.Get("DesignId.Value", DesignId.XmlNamespace);

    /// <summary>Returns the sanitized XAML, or the input unchanged if it isn't well-formed XML (the loader then reports the error).</summary>
    public string Sanitize(string xaml)
    {
        XDocument document;
        try
        {
            // PreserveWhitespace + DisableFormatting keep line numbers identical, so loader errors still point at the right line.
            document = XDocument.Parse(xaml, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return xaml;
        }

        document.Root?.Attribute(XamlNs + "Class")?.Remove();

        foreach (var element in document.Descendants().ToList())
        {
            if (element.Name.LocalName.Contains('.'))
            {
                continue; // property element such as <Grid.RowDefinitions>
            }

            var type = resolver.Resolve(element.Name.NamespaceName, element.Name.LocalName);
            if (type is null)
            {
                continue; // unknown type: leave it, the loader will report it
            }

            if (!typeof(AvaloniaObject).IsAssignableFrom(type))
            {
                element.Attribute(DesignIdAttribute)?.Remove();
            }

            foreach (var attribute in element.Attributes().ToList())
            {
                if (IsEventAttribute(element, type, attribute))
                {
                    attribute.Remove();
                }
            }
        }

        return document.Declaration is null
            ? document.Root!.ToString(SaveOptions.DisableFormatting)
            : document.ToString(SaveOptions.DisableFormatting);
    }

    private bool IsEventAttribute(XElement element, Type elementType, XAttribute attribute)
    {
        if (attribute.IsNamespaceDeclaration)
        {
            return false;
        }

        var name = attribute.Name.LocalName;
        var dot = name.IndexOf('.');
        if (dot < 0)
        {
            // Plain member: only unprefixed attributes can be the element's own events.
            return attribute.Name.Namespace == XNamespace.None && IsEvent(elementType, name);
        }

        // Attached member: Owner.Event, owner resolved in the attribute's namespace (or the element's default one).
        var ownerNamespace = attribute.Name.Namespace == XNamespace.None
            ? element.GetDefaultNamespace().NamespaceName
            : attribute.Name.NamespaceName;
        var owner = resolver.Resolve(ownerNamespace, name[..dot]);
        return owner is not null && IsEvent(owner, name[(dot + 1)..]);
    }

    private static bool IsEvent(Type type, string name)
    {
        if (type.GetEvent(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static) is not null)
        {
            return true;
        }

        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            if (RoutedEventRegistry.Instance.GetRegistered(t).Any(e => e.Name == name))
            {
                return true;
            }
        }

        return false;
    }
}
