using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using XDesigner.Protocol;
using XDesigner.RenderHost.DesignTime;

namespace XDesigner.RenderHost.Rendering;

/// <summary>A render copy ready for <c>LoadFromXaml</c>: the root's type (an instance is loaded into) and the design size.</summary>
internal sealed record SanitizedMauiXaml(string Xaml, Type? RootType, double? DesignWidth, double? DesignHeight);

/// <summary>
/// Last transforms on a MAUI render copy (§5.4, §14.6), done here because only the host can resolve every type:
/// <list type="bullet">
/// <item>a root without namespaces (MAUI 11) gets MAUI's (the app's copy already declares them; a file opened on its own may not);</item>
/// <item><c>x:Class</c> and event attributes go: the runtime loader looks handlers up on the root object;</item>
/// <item>attributes and elements of ignorable namespaces (<c>d:</c>) go, after reading the design size;</item>
/// <item>design ids move to the host's own namespace, and off elements that aren't BindableObjects.</item>
/// </list>
/// </summary>
internal sealed class MauiSanitizer(MauiTypeResolver resolver, System.Reflection.Assembly? userAssembly = null)
{
    private static readonly XNamespace Xaml2009 = "http://schemas.microsoft.com/winfx/2009/xaml";
    private static readonly XNamespace Xaml2006 = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace MarkupCompatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XName DesignIdAttribute = XName.Get(ProtocolInfo.DesignIdAttributeName, DesignId.XmlNamespace);

    public SanitizedMauiXaml Sanitize(string xaml)
    {
        XDocument document;
        try
        {
            document = ParseWithImplicitNamespaces(xaml);
        }
        catch (System.Xml.XmlException)
        {
            return new SanitizedMauiXaml(xaml, null, null, null); // the loader reports the error
        }

        var root = document.Root!;
        var ignorable = IgnorableNamespaces(root);
        var designWidth = DesignSize(root, ignorable, "DesignWidth");
        var designHeight = DesignSize(root, ignorable, "DesignHeight");
        RemoveIgnorable(root, ignorable);
        MapDesignIds(document);
        AddAssemblies(document);
        root.Attribute(Xaml2009 + "Class")?.Remove();
        root.Attribute(Xaml2006 + "Class")?.Remove();

        foreach (var element in document.Descendants().ToList())
        {
            if (element.Name.LocalName.Contains('.'))
            {
                continue; // property element
            }

            if (element.Name.Namespace == Xaml2009 || element.Name.Namespace == Xaml2006)
            {
                element.Attribute(DesignIdAttribute)?.Remove(); // x:String, x:Double…
                continue;
            }

            var type = resolver.Resolve(element.Name.NamespaceName, element.Name.LocalName);
            if (type is null)
            {
                continue;
            }

            if (!typeof(BindableObject).IsAssignableFrom(type))
            {
                element.Attribute(DesignIdAttribute)?.Remove();
            }

            foreach (var attribute in element.Attributes().Where(a => IsEventAttribute(element, type, a)).ToList())
            {
                attribute.Remove();
            }
        }

        var rootType = resolver.Resolve(root.Name.NamespaceName, root.Name.LocalName);
        var line = root is System.Xml.IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 1;
        return new SanitizedMauiXaml(new string('\n', Math.Max(0, line - 1)) + root.ToString(SaveOptions.DisableFormatting), rootType, designWidth, designHeight);
    }

    /// <summary>Parses the copy; a root without a namespace (MAUI 11's implicit ones) gets MAUI's default and <c>x:</c> namespaces.</summary>
    private static XDocument ParseWithImplicitNamespaces(string xaml)
    {
        const LoadOptions options = LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo;
        try
        {
            var document = XDocument.Parse(xaml, options);
            if (document.Root!.Name.Namespace != XNamespace.None)
            {
                return document;
            }
        }
        catch (System.Xml.XmlException)
        {
            // An undeclared x: prefix: parse again below.
        }

        var names = new System.Xml.NameTable();
        var manager = new System.Xml.XmlNamespaceManager(names);
        manager.AddNamespace("", MauiTypeResolver.MauiNamespace);
        manager.AddNamespace("x", Xaml2009.NamespaceName);
        using var reader = System.Xml.XmlReader.Create(
            new StringReader(xaml),
            new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null },
            new System.Xml.XmlParserContext(names, manager, null, System.Xml.XmlSpace.None));
        var parsed = XDocument.Load(reader, options);
        parsed.Root!.SetAttributeValue("xmlns", MauiTypeResolver.MauiNamespace);
        parsed.Root.SetAttributeValue(XNamespace.Xmlns + "x", Xaml2009.NamespaceName);
        return parsed;
    }

    private static HashSet<XNamespace> IgnorableNamespaces(XElement root)
    {
        var result = new HashSet<XNamespace>();
        if (root.Attribute(MarkupCompatibility + "Ignorable")?.Value is { } prefixes)
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

    private static double? DesignSize(XElement root, HashSet<XNamespace> ignorable, string name)
    {
        var value = root.Attributes().FirstOrDefault(a => ignorable.Contains(a.Name.Namespace) && a.Name.LocalName == name)?.Value;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) && size > 0 && double.IsFinite(size) ? size : null;
    }

    private static void RemoveIgnorable(XElement root, HashSet<XNamespace> ignorable)
    {
        root.Attribute(MarkupCompatibility + "Ignorable")?.Remove();
        foreach (var element in root.DescendantsAndSelf().ToList())
        {
            if (element != root && ignorable.Contains(element.Name.Namespace))
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

    /// <summary>
    /// Gives <c>clr-namespace:</c> declarations without an assembly the one the namespace is in (the project's, as MAUI's
    /// compiler assumes; the runtime loader would look in MAUI's).
    /// </summary>
    private void AddAssemblies(XDocument document)
    {
        foreach (var declaration in document.Descendants().SelectMany(e => e.Attributes()).Where(a => a.IsNamespaceDeclaration).ToList())
        {
            if (declaration.Value.StartsWith("clr-namespace:", StringComparison.Ordinal) && !declaration.Value.Contains(";assembly=", StringComparison.Ordinal)
                && MauiTypeResolver.AssemblyOfNamespace(declaration.Value["clr-namespace:".Length..].TrimEnd(';'), userAssembly) is { } assembly)
            {
                var updated = $"{declaration.Value.TrimEnd(';')};assembly={assembly}";
                var owner = declaration.Parent!;
                var old = (XNamespace)declaration.Value;
                declaration.Value = updated;

                // Elements and attributes in the old namespace move to the new one (XLinq names carry the namespace itself).
                XNamespace now = updated;
                foreach (var element in owner.DescendantsAndSelf().Where(e => e.Name.Namespace == old))
                {
                    element.Name = now + element.Name.LocalName;
                }

                foreach (var attribute in owner.DescendantsAndSelf().SelectMany(e => e.Attributes()).Where(a => a.Name.Namespace == old).ToList())
                {
                    var parent = attribute.Parent!;
                    attribute.Remove();
                    parent.SetAttributeValue(now + attribute.Name.LocalName, attribute.Value);
                }
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
