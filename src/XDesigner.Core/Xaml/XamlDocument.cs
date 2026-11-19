using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace XDesigner.Core.Xaml;

/// <summary>What changed in a <see cref="XamlDocument"/>.</summary>
/// <param name="AffectedIds">Ids of elements whose attributes, content or children changed.</param>
/// <param name="StructureChanged">True when elements were added, removed or moved (outline must refresh).</param>
public sealed record XamlDocumentChange(IReadOnlySet<int> AffectedIds, bool StructureChanged);

/// <summary>
/// The designer's in-memory model of one <c>.axaml</c> file, and the source of truth for editing (§5.1).
/// Wraps an <see cref="XDocument"/>, gives every object element a stable <see cref="ElementNode"/> id for
/// the session, and serializes back to text without disturbing anything that wasn't edited.
/// </summary>
public sealed class XamlDocument
{
    private readonly HashSet<XElement> _dirtyTags = [];
    private readonly Dictionary<int, ElementNode> _nodesById = [];
    private int _nextId;
    private int _changeDepth;
    private HashSet<int>? _pendingIds;
    private bool _pendingStructure;
    private XElement? _changingAttributeParent;

    private XamlDocument(XDocument xml, string? prolog, string epilog, string newLine, Encoding encoding)
    {
        Xml = xml;
        Prolog = prolog;
        Epilog = epilog;
        NewLine = newLine;
        Encoding = encoding;
        Xml.Changing += OnXmlChanging;
        Xml.Changed += OnXmlChanged;
    }

    public XDocument Xml { get; }

    /// <summary>Identifies this open document for the session (e.g. to tell whether a clipboard fragment came from it).</summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    public ElementNode Root => NodeFor(Xml.Root!);

    /// <summary>Line ending used by the source (<c>\r\n</c> or <c>\n</c>), reused for generated text.</summary>
    public string NewLine { get; }

    /// <summary>Encoding (including whether it had a BOM) to save with.</summary>
    public Encoding Encoding { get; }

    /// <summary>Original text before the root element (declaration, comments, whitespace); null if the root had no original tag.</summary>
    internal string? Prolog { get; }

    /// <summary>Original whitespace after the root element (usually the final newline).</summary>
    internal string Epilog { get; }

    /// <summary>Monotonic counter, bumped by every change (render versions, dirty tracking).</summary>
    public long Revision { get; private set; }

    /// <summary>Raised after each change (or once per <see cref="BatchChanges"/> scope).</summary>
    public event EventHandler<XamlDocumentChange>? Changed;

    /// <summary>Parses XAML text. Throws <see cref="XmlException"/> for malformed XML.</summary>
    public static XamlDocument Parse(string text, Encoding? encoding = null)
    {
        var xml = XDocument.Parse(text, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        var root = xml.Root ?? throw new XmlException("The document has no root element.");
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var lineStarts = LineStarts(text);
        foreach (var element in xml.Descendants())
        {
            var tag = OriginalStartTag.TryRead(text, StartOffset(element, lineStarts), element);
            if (tag is not null)
            {
                element.AddAnnotation(tag);
            }
        }

        var rootStart = StartOffset(root, lineStarts);
        var prolog = root.Annotation<OriginalStartTag>() is not null && rootStart >= 0 ? text[..rootStart] : null;
        if (prolog is not null)
        {
            OriginalText.Annotate(text, root, rootStart);
        }

        var lastClose = text.LastIndexOf('>');
        var epilog = lastClose >= 0 && string.IsNullOrWhiteSpace(text[(lastClose + 1)..]) ? text[(lastClose + 1)..] : "";

        return new XamlDocument(xml, prolog, epilog, newLine, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Loads a file, remembering its encoding and BOM so <see cref="Save"/> writes it back the same way.</summary>
    public static XamlDocument Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var encoding = DetectEncoding(bytes, out var preambleLength);
        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        return Parse(text, encoding);
    }

    /// <summary>Serializes the document; unedited documents come back byte for byte.</summary>
    public string ToXaml() => XamlTextWriter.Write(this);

    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToXaml(), Encoding);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The render copy sent to the render host (§5.4), with every object element's stable id.</summary>
    public string BuildRenderCopy(IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>>? designOverrides = null) =>
        RenderCopyBuilder.Build(Xml, e => RenderCopyBuilder.IsObjectElement(e) ? NodeFor(e).Id : null, designOverrides);

    /// <summary>The node for an element in this document (created on first use; ids are stable for the session).</summary>
    public ElementNode NodeFor(XElement element)
    {
        var node = element.Annotation<ElementNode>();
        if (node is null)
        {
            node = new ElementNode(this, element, ++_nextId);
            element.AddAnnotation(node);
            _nodesById[node.Id] = node;
        }

        return node;
    }

    /// <summary>Finds a node by id, if its element is still part of the document.</summary>
    public ElementNode? FindById(int id) =>
        _nodesById.TryGetValue(id, out var node) && node.Element.Document == Xml ? node : null;

    /// <summary>All object elements in document order.</summary>
    public IEnumerable<ElementNode> AllNodes =>
        Xml.Root!.DescendantsAndSelf().Where(RenderCopyBuilder.IsObjectElement).Select(NodeFor);

    /// <summary>Groups changes so <see cref="Changed"/> fires once when the returned scope is disposed.</summary>
    public IDisposable BatchChanges()
    {
        _changeDepth++;
        return new BatchScope(this);
    }

    /// <summary>Whether an element's start tag must be rebuilt instead of copied from the source.</summary>
    internal bool IsTagDirty(XElement element) => _dirtyTags.Contains(element);

    private void OnXmlChanging(object? sender, XObjectChangeEventArgs e)
    {
        // A removed attribute has no parent any more by the time Changed fires.
        _changingAttributeParent = sender is XAttribute attribute ? attribute.Parent : null;
    }

    private void OnXmlChanged(object? sender, XObjectChangeEventArgs e)
    {
        var structure = false;
        XElement? affected = null;
        switch (sender)
        {
            case XAttribute attribute:
                affected = attribute.Parent ?? _changingAttributeParent;
                if (affected is not null)
                {
                    _dirtyTags.Add(affected);
                }

                break;
            case XElement element when e.ObjectChange == XObjectChange.Name:
                _dirtyTags.Add(element);
                affected = element;
                break;
            case XElement element:
                affected = element.Parent;
                structure = true;
                break;
            case XNode node:
                affected = node.Parent;
                structure = e.ObjectChange is XObjectChange.Add or XObjectChange.Remove;
                break;
        }

        _changingAttributeParent = null;
        Revision++;
        var id = affected is not null && RenderCopyBuilder.IsObjectElement(affected) ? NodeFor(affected).Id : (int?)null;
        (_pendingIds ??= []).UnionWith(id is { } i ? [i] : []);
        _pendingStructure |= structure;
        if (_changeDepth == 0)
        {
            Flush();
        }
    }

    private void Flush()
    {
        if (_pendingIds is null && !_pendingStructure)
        {
            return;
        }

        var change = new XamlDocumentChange(_pendingIds ?? [], _pendingStructure);
        _pendingIds = null;
        _pendingStructure = false;
        Changed?.Invoke(this, change);
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                starts.Add(i + 1);
            }
            else if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    /// <summary>Offset of the element's <c>&lt;</c>, from the parser's line info (which points at the name).</summary>
    private static int StartOffset(XElement element, List<int> lineStarts)
    {
        var info = (IXmlLineInfo)element;
        if (!info.HasLineInfo() || info.LineNumber < 1 || info.LineNumber > lineStarts.Count)
        {
            return -1;
        }

        return lineStarts[info.LineNumber - 1] + info.LinePosition - 2;
    }

    private static Encoding DetectEncoding(byte[] bytes, out int preambleLength)
    {
        Encoding[] candidates = [new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true)];
        foreach (var candidate in candidates)
        {
            var preamble = candidate.GetPreamble();
            if (preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble))
            {
                preambleLength = preamble.Length;
                return candidate;
            }
        }

        preambleLength = 0;
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    private sealed class BatchScope(XamlDocument document) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (--document._changeDepth == 0)
            {
                document.Flush();
            }
        }
    }
}
