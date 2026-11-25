using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace XDesigner.Protocol.Rendering;

/// <summary>
/// Writes a render copy so that every element and attribute that came from the file is on the line it was on there
/// (§8.5): the loader's error line numbers then point at the file. LINQ to XML writes each start tag on one line, so its
/// own output loses the lines of tags whose attributes span several (review M21). Nodes added since parsing (design ids,
/// namespace declarations) go on the current line.
/// </summary>
public static class LineKeepingXml
{
    /// <summary>
    /// The text of <paramref name="root"/>, with <paramref name="lineOf"/> giving the file line of an element or attribute
    /// (null when it has none). Defaults to the objects' own line info (a document parsed with <c>SetLineInfo</c>).
    /// </summary>
    public static string Write(XElement root, Func<XObject, int?>? lineOf = null)
    {
        lineOf ??= LineInfo;
        DeclareMissingNamespaces(root);
        var writer = new Writer(lineOf);
        writer.Element(root);
        return writer.ToString();
    }

    /// <summary>The line an object was parsed at, if the document was parsed with line info.</summary>
    public static int? LineInfo(XObject node) => node is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : null;

    /// <summary>Elements and attributes in a namespace nothing declares get a declaration (LINQ to XML would invent one too).</summary>
    private static void DeclareMissingNamespaces(XElement root)
    {
        var next = 1;
        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Name.Namespace == XNamespace.None && element.GetDefaultNamespace() != XNamespace.None)
            {
                element.SetAttributeValue("xmlns", ""); // keep it out of the inherited default namespace
            }

            var namespaces = element.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => a.Name.Namespace).Prepend(element.Name.Namespace);
            foreach (var ns in namespaces.Distinct())
            {
                if (ns == XNamespace.None || ns == XNamespace.Xml || ns == XNamespace.Xmlns
                    || element.GetPrefixOfNamespace(ns) is not null
                    || (ns == element.Name.Namespace && element.GetDefaultNamespace() == ns))
                {
                    continue;
                }

                string prefix;
                do
                {
                    prefix = "p" + next++.ToString(CultureInfo.InvariantCulture);
                }
                while (element.GetNamespaceOfPrefix(prefix) is not null);

                element.SetAttributeValue(XNamespace.Xmlns + prefix, ns.NamespaceName);
            }
        }
    }

    private sealed class Writer(Func<XObject, int?> lineOf)
    {
        private readonly StringBuilder _out = new();
        private int _line = 1;

        public override string ToString() => _out.ToString();

        public void Element(XElement element)
        {
            MoveTo(lineOf(element));
            _out.Append('<').Append(ElementName(element));
            foreach (var attribute in element.Attributes())
            {
                if (lineOf(attribute) is { } line && line > _line)
                {
                    MoveTo(line);
                }

                _out.Append(' ').Append(AttributeName(element, attribute)).Append("=\"").Append(EscapeAttribute(attribute.Value)).Append('"');
            }

            if (!element.Nodes().Any())
            {
                _out.Append(" />");
                return;
            }

            _out.Append('>');
            foreach (var node in element.Nodes())
            {
                switch (node)
                {
                    case XElement child:
                        Element(child);
                        break;
                    case XCData cdata:
                        Append("<![CDATA[" + cdata.Value + "]]>");
                        break;
                    case XText text:
                        Append(EscapeText(text.Value));
                        break;
                    case XComment comment:
                        Append("<!--" + comment.Value + "-->");
                        break;
                    case XProcessingInstruction pi:
                        Append("<?" + pi.Target + (pi.Data.Length > 0 ? " " + pi.Data : "") + "?>");
                        break;
                }
            }

            _out.Append("</").Append(ElementName(element)).Append('>');
        }

        private void MoveTo(int? line)
        {
            for (; line is { } target && _line < target; _line++)
            {
                _out.Append('\n');
            }
        }

        private void Append(string text)
        {
            _out.Append(text);
            foreach (var c in text)
            {
                if (c == '\n')
                {
                    _line++;
                }
            }
        }

        private static string ElementName(XElement element)
        {
            var ns = element.Name.Namespace;
            if (ns == XNamespace.None || element.GetDefaultNamespace() == ns)
            {
                return element.Name.LocalName;
            }

            return element.GetPrefixOfNamespace(ns) is { } prefix ? $"{prefix}:{element.Name.LocalName}" : element.Name.LocalName;
        }

        private static string AttributeName(XElement element, XAttribute attribute)
        {
            if (attribute.IsNamespaceDeclaration)
            {
                return attribute.Name.Namespace == XNamespace.None ? "xmlns" : "xmlns:" + attribute.Name.LocalName;
            }

            var ns = attribute.Name.Namespace;
            if (ns == XNamespace.None)
            {
                return attribute.Name.LocalName;
            }

            if (ns == XNamespace.Xml)
            {
                return "xml:" + attribute.Name.LocalName;
            }

            return element.GetPrefixOfNamespace(ns) is { } prefix ? $"{prefix}:{attribute.Name.LocalName}" : attribute.Name.LocalName;
        }

        private static string EscapeText(string value) =>
            value.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("\r", "&#xD;", StringComparison.Ordinal);

        private static string EscapeAttribute(string value) =>
            value.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal)
                .Replace("\r", "&#xD;", StringComparison.Ordinal)
                .Replace("\n", "&#xA;", StringComparison.Ordinal)
                .Replace("\t", "&#x9;", StringComparison.Ordinal);
    }
}
