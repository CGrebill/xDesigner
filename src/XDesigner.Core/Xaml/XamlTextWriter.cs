using System.Text;
using System.Xml.Linq;

namespace XDesigner.Core.Xaml;

/// <summary>
/// Serializes a <see cref="XamlDocument"/> back to text, reusing the original source text wherever the
/// document wasn't changed: the prolog, every unedited start tag, text, comment and CDATA node verbatim
/// (<see cref="OriginalText"/>), and for edited start tags the original text of each unchanged attribute
/// (§5.1, §5.5). <see cref="XDocument.ToString()"/> would instead collapse multi-line attribute lists,
/// change quote styles and re-escape text.
/// </summary>
internal sealed class XamlTextWriter
{
    private readonly XamlDocument _document;
    private readonly StringBuilder _out = new();
    private readonly string _newLine;

    private XamlTextWriter(XamlDocument document)
    {
        _document = document;
        _newLine = document.NewLine;
    }

    public static string Write(XamlDocument document) => new XamlTextWriter(document).WriteDocument();

    private string WriteDocument()
    {
        var xml = _document.Xml;
        var root = xml.Root!;

        // Before the root: the original text verbatim (declaration, comments, whitespace) when there is one.
        if (_document.Prolog is { } prolog)
        {
            _out.Append(prolog);
        }
        else
        {
            if (xml.Declaration is { } declaration)
            {
                _out.Append(declaration).Append(_newLine);
            }

            foreach (var node in xml.Nodes().TakeWhile(n => n != root))
            {
                WriteNode(node);
            }
        }

        WriteElement(root);

        // After the root: comments/PIs and the whitespace between them, then the original trailing whitespace (the
        // final newline, which the epilog holds).
        var after = root.NodesAfterSelf().ToList();
        for (var i = 0; i < after.Count; i++)
        {
            var isWhitespace = after[i] is XText text && string.IsNullOrWhiteSpace(text.Value);
            if (!isWhitespace || (i < after.Count - 1 && OriginalText.Of(after[i]) is not null))
            {
                WriteNode(after[i]);
            }
        }

        _out.Append(_document.Epilog);
        return _out.ToString();
    }

    private void WriteNode(XNode node)
    {
        if (OriginalText.Of(node) is { } original)
        {
            _out.Append(original); // unchanged since it was parsed: exactly as written
            return;
        }

        switch (node)
        {
            case XElement element:
                WriteElement(element);
                break;
            case XCData cdata:
                _out.Append("<![CDATA[").Append(NormalizeNewLines(cdata.Value)).Append("]]>");
                break;
            case XText text:
                // Escape first: a carriage return in the value (from &#13;) must stay a reference, not become a line break.
                _out.Append(NormalizeNewLines(EscapeText(text.Value)));
                break;
            case XComment comment:
                _out.Append("<!--").Append(NormalizeNewLines(comment.Value)).Append("-->");
                break;
            case XProcessingInstruction pi:
                _out.Append("<?").Append(pi.Target).Append(pi.Data.Length > 0 ? " " : "").Append(pi.Data).Append("?>");
                break;
        }
    }

    private void WriteElement(XElement element)
    {
        var hasContent = element.Nodes().Any();
        _out.Append(StartTag(element, hasContent));
        if (!hasContent && IsSelfClosingOutput(element))
        {
            return;
        }

        foreach (var child in element.Nodes())
        {
            WriteNode(child);
        }

        _out.Append("</").Append(QualifiedName(element, element.Name)).Append('>');
    }

    /// <summary>Whether the start tag just written ended with <c>/&gt;</c>.</summary>
    private static bool IsSelfClosingOutput(XElement element)
    {
        // Generated tags close themselves when empty; original tags keep their own style, edited or not.
        var original = element.Annotation<OriginalStartTag>();
        return original is null || original.SelfClosing;
    }

    private string StartTag(XElement element, bool hasContent)
    {
        var original = element.Annotation<OriginalStartTag>();
        if (original is null)
        {
            return GenerateStartTag(element, hasContent);
        }

        if (!_document.IsTagDirty(element))
        {
            if (original.SelfClosing && hasContent)
            {
                // Children were added to a formerly empty element: <X a="1" /> becomes <X a="1">.
                return original.Text[..^2].TrimEnd() + ">";
            }

            return original.Text; // not self-closing but now empty: "<X ...></X>" is still fine
        }

        return RebuildStartTag(element, original, hasContent);
    }

    private static string RebuildStartTag(XElement element, OriginalStartTag original, bool hasContent)
    {
        var sb = new StringBuilder();
        var expectedName = "<" + QualifiedName(element, element.Name);
        sb.Append(original.NameText == expectedName ? original.NameText : expectedName);

        var originalByName = original.Attributes.ToDictionary(a => a.Name);
        var lastLeading = original.Attributes.LastOrDefault()?.Leading;
        var newAttributeLeading = lastLeading is not null && lastLeading.Contains('\n') ? lastLeading : " ";

        foreach (var attribute in element.Attributes())
        {
            var name = AttributeName(element, attribute);
            if (originalByName.TryGetValue(attribute.Name, out var was))
            {
                sb.Append(was.Leading);
                sb.Append(was.Value == attribute.Value ? was.Text : $"{name}={was.Quote}{EscapeAttribute(attribute.Value, was.Quote)}{was.Quote}");
            }
            else
            {
                sb.Append(newAttributeLeading).Append(name).Append("=\"").Append(EscapeAttribute(attribute.Value, '"')).Append('"');
            }
        }

        if (hasContent)
        {
            sb.Append(original.SelfClosing ? "" : original.TailWhitespace).Append('>');
        }
        else if (original.SelfClosing)
        {
            sb.Append(original.TailWhitespace).Append("/>");
        }
        else
        {
            sb.Append(original.TailWhitespace).Append('>'); // "<X ...></X>" stays that way
        }

        return sb.ToString();
    }

    private static string GenerateStartTag(XElement element, bool hasContent)
    {
        var sb = new StringBuilder("<").Append(QualifiedName(element, element.Name));
        foreach (var attribute in element.Attributes())
        {
            sb.Append(' ').Append(AttributeName(element, attribute)).Append("=\"").Append(EscapeAttribute(attribute.Value, '"')).Append('"');
        }

        return sb.Append(hasContent ? ">" : " />").ToString();
    }

    private static string QualifiedName(XElement element, XName name)
    {
        if (name.Namespace == XNamespace.None)
        {
            return name.LocalName;
        }

        var prefix = element.GetPrefixOfNamespace(name.Namespace);
        if (prefix is null && element.GetDefaultNamespace() == name.Namespace)
        {
            return name.LocalName;
        }

        return prefix is null ? name.LocalName : $"{prefix}:{name.LocalName}";
    }

    private static string AttributeName(XElement element, XAttribute attribute)
    {
        if (attribute.IsNamespaceDeclaration)
        {
            return attribute.Name.Namespace == XNamespace.None ? "xmlns" : $"xmlns:{attribute.Name.LocalName}";
        }

        if (attribute.Name.Namespace == XNamespace.None)
        {
            return attribute.Name.LocalName;
        }

        if (attribute.Name.Namespace == XNamespace.Xml)
        {
            return $"xml:{attribute.Name.LocalName}";
        }

        var prefix = element.GetPrefixOfNamespace(attribute.Name.Namespace);
        return prefix is null ? attribute.Name.LocalName : $"{prefix}:{attribute.Name.LocalName}";
    }

    private string NormalizeNewLines(string value) =>
        _newLine == "\n" ? value : value.Replace("\n", _newLine, StringComparison.Ordinal);

    private static string EscapeText(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\r", "&#xD;", StringComparison.Ordinal);

    private static string EscapeAttribute(string value, char quote)
    {
        var escaped = value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace("\r", "&#xD;", StringComparison.Ordinal)
            .Replace("\n", "&#xA;", StringComparison.Ordinal)
            .Replace("\t", "&#x9;", StringComparison.Ordinal);
        return quote == '"'
            ? escaped.Replace("\"", "&quot;", StringComparison.Ordinal)
            : escaped.Replace("'", "&apos;", StringComparison.Ordinal);
    }
}
