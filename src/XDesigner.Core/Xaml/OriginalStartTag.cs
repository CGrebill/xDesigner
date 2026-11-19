using System.Xml.Linq;

namespace XDesigner.Core.Xaml;

/// <summary>
/// The exact source text of an element's start tag, split into pieces so that an edited tag can be
/// rebuilt while keeping the original layout of every attribute that didn't change (§5.1, §5.5).
/// Attached to elements as an annotation when a document is parsed.
/// </summary>
internal sealed class OriginalStartTag
{
    private OriginalStartTag(string text, string nameText, List<Attr> attributes, string tailWhitespace, bool selfClosing)
    {
        Text = text;
        NameText = nameText;
        Attributes = attributes;
        TailWhitespace = tailWhitespace;
        SelfClosing = selfClosing;
    }

    /// <summary>The whole tag, <c>&lt;</c> to <c>&gt;</c>.</summary>
    public string Text { get; }

    /// <summary><c>&lt;prefix:Name</c> as written.</summary>
    public string NameText { get; }

    public List<Attr> Attributes { get; }

    /// <summary>Whitespace between the last attribute and <c>/&gt;</c> or <c>&gt;</c>.</summary>
    public string TailWhitespace { get; }

    public bool SelfClosing { get; }

    /// <summary>One attribute as written: <paramref name="Leading"/> whitespace, then <paramref name="Text"/> (<c>name="value"</c>).</summary>
    public sealed record Attr(XName Name, string Value, string Leading, string Text, char Quote);

    /// <summary>
    /// Parses the start tag that begins at <paramref name="start"/> (the <c>&lt;</c>) in <paramref name="source"/>.
    /// Returns null if the text doesn't look like the start tag of <paramref name="element"/> (then the tag is regenerated).
    /// </summary>
    public static OriginalStartTag? TryRead(string source, int start, XElement element)
    {
        if (start < 0 || start >= source.Length || source[start] != '<')
        {
            return null;
        }

        var i = start + 1;
        while (i < source.Length && !char.IsWhiteSpace(source[i]) && source[i] is not ('/' or '>'))
        {
            i++;
        }

        var nameText = source[start..i];
        var attributes = new List<Attr>();
        while (i < source.Length)
        {
            var wsStart = i;
            while (i < source.Length && char.IsWhiteSpace(source[i]))
            {
                i++;
            }

            var leading = source[wsStart..i];
            if (i >= source.Length)
            {
                return null;
            }

            if (source[i] == '>' || (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '>'))
            {
                var selfClosing = source[i] == '/';
                var end = selfClosing ? i + 2 : i + 1;
                return new OriginalStartTag(source[start..end], nameText, attributes, leading, selfClosing);
            }

            var attrStart = i;
            while (i < source.Length && !char.IsWhiteSpace(source[i]) && source[i] != '=')
            {
                i++;
            }

            var qualifiedName = source[attrStart..i];
            while (i < source.Length && char.IsWhiteSpace(source[i]))
            {
                i++;
            }

            if (i >= source.Length || source[i] != '=')
            {
                return null;
            }

            i++;
            while (i < source.Length && char.IsWhiteSpace(source[i]))
            {
                i++;
            }

            if (i >= source.Length || source[i] is not ('"' or '\''))
            {
                return null;
            }

            var quote = source[i];
            var close = source.IndexOf(quote, i + 1);
            if (close < 0)
            {
                return null;
            }

            i = close + 1;
            var name = ResolveName(qualifiedName, element);
            var value = element.Attribute(name)?.Value;
            if (value is null)
            {
                return null; // couldn't match the attribute; play safe and regenerate
            }

            attributes.Add(new Attr(name, value, leading, source[attrStart..i], quote));
        }

        return null;
    }

    private static XName ResolveName(string qualifiedName, XElement element)
    {
        if (qualifiedName == "xmlns")
        {
            return "xmlns";
        }

        var colon = qualifiedName.IndexOf(':');
        if (colon < 0)
        {
            return qualifiedName;
        }

        var prefix = qualifiedName[..colon];
        var local = qualifiedName[(colon + 1)..];
        if (prefix == "xmlns")
        {
            return XNamespace.Xmlns + local;
        }

        if (prefix == "xml")
        {
            return XNamespace.Xml + local;
        }

        return (element.GetNamespaceOfPrefix(prefix) ?? XNamespace.None) + local;
    }
}
