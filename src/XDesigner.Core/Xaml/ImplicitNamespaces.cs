using System.Xml;
using System.Xml.Linq;
using XDesigner.Core.Frameworks;

namespace XDesigner.Core.Xaml;

/// <summary>
/// Namespaces a XAML file uses without declaring them (§5.3). .NET MAUI 11 projects write <c>&lt;ContentPage x:Class="…"&gt;</c>
/// with no <c>xmlns</c> at all: the default namespace is MAUI's and <c>x:</c> is the 2009 XAML namespace. Such files are
/// parsed with those predeclared and carry this annotation, so prefixes are resolved and written as the file has them,
/// and no declaration is ever added for them.
/// </summary>
public sealed class ImplicitNamespaces
{
    /// <summary>MAUI's implicit namespaces: the default one and <c>x:</c>.</summary>
    public static readonly ImplicitNamespaces Maui = new(new Dictionary<string, XNamespace>
    {
        [""] = FrameworkProfile.MauiNamespace,
        ["x"] = FrameworkProfile.Xaml2009Namespace,
    });

    private readonly Dictionary<string, XNamespace> _byPrefix;

    private ImplicitNamespaces(Dictionary<string, XNamespace> byPrefix)
    {
        _byPrefix = byPrefix;
    }

    /// <summary>Prefix → namespace; the empty prefix is the default namespace.</summary>
    public IReadOnlyDictionary<string, XNamespace> ByPrefix => _byPrefix;

    /// <summary>
    /// Parses XAML text: as written, or, when the root has no namespace (or a prefix such as <c>x:</c> is undeclared),
    /// with MAUI's implicit namespaces predeclared. Throws <see cref="XmlException"/> for malformed XML.
    /// </summary>
    public static XDocument Parse(string text, LoadOptions options)
    {
        XmlException? first = null;
        try
        {
            var document = XDocument.Parse(text, options);
            if (document.Root?.Name.Namespace != XNamespace.None)
            {
                return document;
            }
        }
        catch (XmlException ex)
        {
            first = ex;
        }

        var names = new NameTable();
        var manager = new XmlNamespaceManager(names);
        foreach (var (prefix, ns) in Maui._byPrefix)
        {
            manager.AddNamespace(prefix, ns.NamespaceName);
        }

        try
        {
            using var reader = XmlReader.Create(
                new StringReader(text),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null },
                new XmlParserContext(names, manager, null, XmlSpace.None));
            var implicitDocument = XDocument.Load(reader, options);
            implicitDocument.AddAnnotation(Maui);
            return implicitDocument;
        }
        catch (XmlException) when (first is not null)
        {
            throw first; // malformed for another reason: report what plain XML parsing found
        }
    }

    /// <summary>The implicit namespaces of the document an element belongs to, if any.</summary>
    public static ImplicitNamespaces? Of(XElement element) => element.Document?.Annotation<ImplicitNamespaces>();
}

/// <summary>Namespace lookups that also know a document's <see cref="ImplicitNamespaces"/>.</summary>
public static class XamlNamespaceExtensions
{
    /// <summary>Like <see cref="XElement.GetPrefixOfNamespace"/>, also finding an implicit prefix (<c>x</c>) the file uses undeclared.</summary>
    public static string? PrefixOf(this XElement element, XNamespace ns)
    {
        if (element.GetPrefixOfNamespace(ns) is { } declared)
        {
            return declared;
        }

        return ImplicitNamespaces.Of(element)?.ByPrefix.FirstOrDefault(p => p.Key.Length > 0 && p.Value == ns && element.GetNamespaceOfPrefix(p.Key) is null).Key;
    }

    /// <summary>Like <see cref="XElement.GetNamespaceOfPrefix"/>, also resolving implicit prefixes.</summary>
    public static XNamespace? NamespaceOf(this XElement element, string prefix) =>
        element.GetNamespaceOfPrefix(prefix)
        ?? (ImplicitNamespaces.Of(element)?.ByPrefix.TryGetValue(prefix, out var ns) == true ? ns : null);

    /// <summary>The default namespace in scope: declared with <c>xmlns</c>, else the implicit one.</summary>
    public static XNamespace DefaultNamespaceOf(this XElement element)
    {
        var declared = element.AncestorsAndSelf().SelectMany(e => e.Attributes()).FirstOrDefault(a => a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.None);
        if (declared is not null)
        {
            return declared.Value;
        }

        return ImplicitNamespaces.Of(element)?.ByPrefix.GetValueOrDefault("") ?? XNamespace.None;
    }
}
