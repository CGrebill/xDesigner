using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace XDesigner.Core.Xaml;

/// <summary>
/// The exact source text of a text, CDATA, comment or processing-instruction node, and the node's value when it was
/// parsed. <see cref="XamlTextWriter"/> writes <see cref="Text"/> verbatim while the node still has that value, so
/// element text keeps its entities (<c>&amp;quot;</c>, <c>&amp;#160;</c>), unescaped <c>&gt;</c> and line endings (§5.1).
/// </summary>
internal sealed class OriginalText(string text, string key)
{
    public string Text { get; } = text;

    /// <summary>The node's <see cref="KeyOf"/> when it was parsed.</summary>
    public string Key { get; } = key;

    /// <summary>What identifies a node's content, compared to decide whether its original text is still valid.</summary>
    public static string? KeyOf(XNode node) => node switch
    {
        XText text => text.Value, // includes XCData
        XComment comment => comment.Value,
        XProcessingInstruction pi => pi.Target + "\n" + pi.Data,
        _ => null,
    };

    /// <summary>The original text of <paramref name="node"/>, if it has one and its content hasn't changed since.</summary>
    public static string? Of(XNode node) =>
        node.Annotation<OriginalText>() is { } original && original.Key == KeyOf(node) ? original.Text : null;

    /// <summary>
    /// Annotates the nodes from the root element onwards with their source text, by walking the source alongside the
    /// tree. Start tags must already carry their <see cref="OriginalStartTag"/>. If the source and the tree ever
    /// disagree, nothing is annotated, and the writer re-serializes text nodes as before.
    /// </summary>
    public static void Annotate(string source, XElement root, int rootStart)
    {
        var annotated = new List<XNode>();
        var pos = rootStart;
        var ok = Element(source, root, ref pos, annotated) && Content(source, root.NodesAfterSelf(), ref pos, annotated, topLevel: true);
        if (!ok)
        {
            foreach (var node in annotated)
            {
                node.RemoveAnnotations<OriginalText>();
            }
        }
    }

    private static bool Element(string source, XElement element, ref int pos, List<XNode> annotated)
    {
        if (element.Annotation<OriginalStartTag>() is not { } tag || string.CompareOrdinal(source, pos, tag.Text, 0, tag.Text.Length) != 0)
        {
            return false;
        }

        pos += tag.Text.Length;
        if (tag.SelfClosing)
        {
            return !element.Nodes().Any();
        }

        if (!Content(source, element.Nodes(), ref pos, annotated, topLevel: false) || !At(source, pos, "</"))
        {
            return false;
        }

        var close = source.IndexOf('>', pos);
        if (close < 0)
        {
            return false;
        }

        pos = close + 1;
        return true;
    }

    private static bool Content(string source, IEnumerable<XNode> nodes, ref int pos, List<XNode> annotated, bool topLevel)
    {
        foreach (var node in nodes)
        {
            if (topLevel && node is not XText)
            {
                // Whitespace outside the root may not be part of the tree.
                while (pos < source.Length && char.IsWhiteSpace(source[pos]))
                {
                    pos++;
                }
            }

            var start = pos;
            switch (node)
            {
                case XElement element:
                    if (!Element(source, element, ref pos, annotated))
                    {
                        return false;
                    }

                    continue;
                case XCData:
                    if (!Delimited(source, ref pos, "<![CDATA[", "]]>"))
                    {
                        return false;
                    }

                    break;
                case XText text:
                    var end = source.IndexOf('<', pos);
                    pos = end < 0 ? source.Length : end;
                    if (Decode(source[start..pos]) != text.Value)
                    {
                        return false;
                    }

                    break;
                case XComment:
                    if (!Delimited(source, ref pos, "<!--", "-->"))
                    {
                        return false;
                    }

                    break;
                case XProcessingInstruction:
                    if (!Delimited(source, ref pos, "<?", "?>"))
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }

            node.AddAnnotation(new OriginalText(source[start..pos], KeyOf(node)!));
            annotated.Add(node);
        }

        return true;
    }

    private static bool Delimited(string source, ref int pos, string open, string close)
    {
        if (!At(source, pos, open))
        {
            return false;
        }

        var end = source.IndexOf(close, pos + open.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        pos = end + close.Length;
        return true;
    }

    private static bool At(string source, int pos, string text) =>
        string.CompareOrdinal(source, pos, text, 0, text.Length) == 0;

    /// <summary>The value an XML parser gives literal text: line endings normalized to <c>\n</c>, references expanded.</summary>
    internal static string Decode(string raw)
    {
        var text = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!text.Contains('&', StringComparison.Ordinal))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var semicolon = text[i] == '&' ? text.IndexOf(';', i) : -1;
            if (semicolon < 0 || Reference(text[(i + 1)..semicolon]) is not { } value)
            {
                sb.Append(text[i]);
                continue;
            }

            sb.Append(value);
            i = semicolon;
        }

        return sb.ToString();
    }

    private static string? Reference(string name)
    {
        switch (name)
        {
            case "lt": return "<";
            case "gt": return ">";
            case "amp": return "&";
            case "quot": return "\"";
            case "apos": return "'";
        }

        if (name.Length < 2 || name[0] != '#')
        {
            return null;
        }

        var hex = name[1] is 'x' or 'X';
        var digits = hex ? name[2..] : name[1..];
        return int.TryParse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var code)
               && code is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF)
            ? char.ConvertFromUtf32(code)
            : null;
    }
}
