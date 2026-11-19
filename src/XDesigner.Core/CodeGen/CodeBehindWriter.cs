using System.Text;
using XDesigner.Protocol.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XDesigner.Core.CodeGen;

/// <summary>The C# shape of a handler for one event: <c>(sender, e)</c> parameter types.</summary>
/// <param name="ArgsBaseTypes">Simple names of the args type's base classes; a method taking one of these also fits.</param>
public sealed record HandlerSignature(string SenderType, string ArgsType, string ArgsNamespace, IReadOnlyList<string> ArgsBaseTypes)
{
    public static HandlerSignature From(EventInfoDto e) => new(e.SenderType, e.ArgsType, e.ArgsNamespace, e.ArgsBaseTypes);
}

/// <summary>Where a handler is: <paramref name="Line"/> is 1-based, the line of the method's signature.</summary>
/// <param name="Created">True when the method was added; false when an existing method was reused.</param>
public sealed record HandlerResult(string FilePath, string ClassName, string MethodName, int Line, bool Created);

/// <summary>The code-behind file can't take a handler (no such class, unreadable, not C#).</summary>
public sealed class CodeBehindException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Adds event handlers to a <c>.axaml.cs</c> file (§10.1 step 5) without disturbing anything else in it: the method
/// text is spliced in before the class's closing brace, matching the file's indentation and line endings, so the rest
/// of the file stays byte for byte as it was. Encoding and BOM are kept; the write is atomic.
/// </summary>
public static class CodeBehindWriter
{
    /// <summary>The code-behind file of a XAML file: <c>MainWindow.axaml</c> → <c>MainWindow.axaml.cs</c>.</summary>
    public static string CodeBehindPathFor(string xamlPath) => xamlPath + ".cs";

    /// <summary>
    /// Makes sure <paramref name="classFullName"/> has a method <paramref name="methodName"/> that can handle an event with
    /// <paramref name="signature"/>. Reuses a compatible existing method; if the name is taken by something incompatible,
    /// uses <c>name_1</c>, <c>name_2</c>… instead. Writes the file only when something was added.
    /// Other files in the same folder that declare the same partial class count too: a compatible method there is reused,
    /// and their member names are taken, as are <paramref name="fieldNames"/> (the XAML's <c>x:Name</c>s, which become fields).
    /// </summary>
    public static HandlerResult EnsureHandler(
        string path, string classFullName, string methodName, HandlerSignature signature, IReadOnlySet<string>? fieldNames = null)
    {
        var (text, encoding) = Read(path);
        var (updated, result) = EnsureHandlerInText(text, path, classFullName, methodName, signature, OtherParts(path, classFullName), fieldNames);
        if (result.Created)
        {
            WriteAtomically(path, updated, encoding);
        }

        return result;
    }

    /// <summary>Methods of the class that could handle the event (for the Events tab's list), with their lines.</summary>
    public static IReadOnlyList<(string Name, int Line)> CompatibleMethods(string path, string classFullName, HandlerSignature signature)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var (text, _) = Read(path);
        var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
        return FindClass(root, classFullName) is { } cls
            ? cls.Members.OfType<MethodDeclarationSyntax>().Where(m => IsCompatible(m, signature))
                .Select(m => (m.Identifier.Text, LineOf(m.Identifier.SpanStart, text))).ToList()
            : [];
    }

    /// <summary>
    /// Creates a minimal code-behind file for a XAML file that has an <c>x:Class</c> but no <c>.axaml.cs</c> yet
    /// (the shape Avalonia's templates use).
    /// </summary>
    public static void CreateCodeBehind(string path, string classFullName, string baseType, string? baseNamespace, string newLine = "\r\n")
    {
        var lastDot = classFullName.LastIndexOf('.');
        var ns = lastDot > 0 ? classFullName[..lastDot] : null;
        var name = classFullName[(lastDot + 1)..];
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(baseNamespace) && baseNamespace != ns)
        {
            sb.Append("using ").Append(baseNamespace).Append(';').Append(newLine).Append(newLine);
        }

        if (ns is not null)
        {
            sb.Append("namespace ").Append(ns).Append(';').Append(newLine).Append(newLine);
        }

        sb.Append("public partial class ").Append(name).Append(" : ").Append(baseType).Append(newLine)
            .Append('{').Append(newLine)
            .Append("    public ").Append(name).Append("()").Append(newLine)
            .Append("    {").Append(newLine)
            .Append("        InitializeComponent();").Append(newLine)
            .Append("    }").Append(newLine)
            .Append('}').Append(newLine);
        WriteAtomically(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>Another file declaring part of the same partial class.</summary>
    internal sealed record ClassPart(string Path, string Text, ClassDeclarationSyntax Class);

    internal static (string Text, HandlerResult Result) EnsureHandlerInText(
        string text, string path, string classFullName, string methodName, HandlerSignature signature,
        IReadOnlyList<ClassPart>? otherParts = null, IReadOnlySet<string>? fieldNames = null)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
        if (root.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error) is { } error)
        {
            // Roslyn's error recovery can put the class's closing brace anywhere: don't splice a method into a guess.
            var line = error.Location.GetLineSpan().StartLinePosition.Line + 1;
            throw new CodeBehindException($"{Path.GetFileName(path)} has a syntax error on line {line} ({error.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}). Fix it, then try again.");
        }

        var cls = FindClass(root, classFullName)
            ?? throw new CodeBehindException($"{Path.GetFileName(path)} has no class {classFullName} (the x:Class of the XAML file).");
        var parts = otherParts ?? [];

        // Reuse a compatible method with the name (in any part of the class); otherwise find a free name.
        var name = methodName;
        for (var n = 1; ; n++)
        {
            var taken = cls.Members.Where(m => MemberName(m) == name).ToList();
            if (taken.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => IsCompatible(m, signature)) is { } existing)
            {
                return (text, new HandlerResult(path, classFullName, name, LineOf(existing.Identifier.SpanStart, text), false));
            }

            foreach (var part in parts)
            {
                if (part.Class.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == name && IsCompatible(m, signature)) is { } elsewhere)
                {
                    return (text, new HandlerResult(part.Path, classFullName, name, LineOf(elsewhere.Identifier.SpanStart, part.Text), false));
                }
            }

            var takenElsewhere = fieldNames?.Contains(name) == true || parts.Any(p => p.Class.Members.Any(m => MemberName(m) == name));
            if (taken.Count == 0 && !takenElsewhere)
            {
                break;
            }

            name = $"{methodName}_{n}";
        }

        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        // 1. The using for the args type, if the file doesn't already see its namespace.
        if (!IsNamespaceVisible(root, cls, signature.ArgsNamespace))
        {
            text = AddUsing(text, root, signature.ArgsNamespace, newLine);
            root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
            cls = FindClass(root, classFullName)!;
        }

        // 2. The method, just before the class's closing brace.
        var indent = MemberIndent(cls, text);
        var close = cls.CloseBraceToken.SpanStart;
        var lineStart = text.LastIndexOf('\n', Math.Max(0, close - 1)) + 1;
        var closeOnOwnLine = string.IsNullOrWhiteSpace(text[lineStart..close]);
        var method = new StringBuilder();
        if (!closeOnOwnLine)
        {
            method.Append(newLine);
        }

        if (cls.Members.Count > 0)
        {
            method.Append(newLine); // blank line after the previous member
        }

        var signatureStart = method.Length;
        method.Append(indent).Append("private void ").Append(name)
            .Append('(').Append(signature.SenderType).Append(" sender, ").Append(signature.ArgsType).Append(" e)").Append(newLine)
            .Append(indent).Append('{').Append(newLine)
            .Append(indent).Append('}').Append(newLine);
        if (!closeOnOwnLine)
        {
            method.Append(ClassIndent(cls, text));
        }

        var insertAt = closeOnOwnLine ? lineStart : close;
        var updated = text.Insert(insertAt, method.ToString());
        return (updated, new HandlerResult(path, classFullName, name, LineOf(insertAt + signatureStart, updated), true));
    }

    /// <summary>The other <c>.cs</c> files next to <paramref name="path"/> that declare part of the partial class.</summary>
    private static List<ClassPart> OtherParts(string path, string classFullName)
    {
        var parts = new List<ClassPart>();
        var simpleName = classFullName[(classFullName.LastIndexOf('.') + 1)..];
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(path))!, "*.cs").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return parts;
        }

        foreach (var file in files.Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var (text, _) = Read(file);
                if (!text.Contains("partial", StringComparison.Ordinal) || !text.Contains(simpleName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (FindClass(CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot(), classFullName) is { } cls
                    && cls.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    parts.Add(new ClassPart(file, text, cls));
                }
            }
            catch (CodeBehindException)
            {
                // Unreadable: it can't be checked, and it isn't written.
            }
        }

        return parts;
    }

    private static ClassDeclarationSyntax? FindClass(CompilationUnitSyntax root, string fullName) =>
        root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => FullNameOf(c) == fullName);

    private static string FullNameOf(ClassDeclarationSyntax cls)
    {
        var parts = new List<string> { cls.Identifier.Text };
        for (var node = cls.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case ClassDeclarationSyntax outer:
                    parts.Add(outer.Identifier.Text);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    parts.Add(ns.Name.ToString());
                    break;
            }
        }

        parts.Reverse();
        return string.Join('.', parts);
    }

    private static string? MemberName(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        EventDeclarationSyntax e => e.Identifier.Text,
        BaseFieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        BaseTypeDeclarationSyntax t => t.Identifier.Text,
        _ => null,
    };

    /// <summary><c>void M(object? sender, TArgs e)</c> where TArgs is the event's args type or one of its bases.</summary>
    private static bool IsCompatible(MethodDeclarationSyntax method, HandlerSignature signature)
    {
        if (method.ReturnType is not PredefinedTypeSyntax { Keyword.Text: "void" } || method.ParameterList.Parameters is not [var sender, var e])
        {
            return false;
        }

        var senderName = SimpleName(sender.Type);
        var senderOk = signature.SenderType.StartsWith("object", StringComparison.Ordinal)
            ? senderName is "object" or "Object"
            : senderName is "object" or "Object" || senderName == SimpleName(signature.SenderType);
        var argsName = SimpleName(e.Type);
        return senderOk && (argsName == SimpleName(signature.ArgsType) || signature.ArgsBaseTypes.Contains(argsName) || argsName == "EventArgs");
    }

    /// <summary>Rightmost identifier of a type, without nullability or generic arguments: <c>Avalonia.Interactivity.RoutedEventArgs?</c> → <c>RoutedEventArgs</c>.</summary>
    private static string SimpleName(TypeSyntax? type) => type switch
    {
        null => "",
        NullableTypeSyntax n => SimpleName(n.ElementType),
        QualifiedNameSyntax q => SimpleName(q.Right),
        AliasQualifiedNameSyntax a => SimpleName(a.Name),
        GenericNameSyntax g => g.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        PredefinedTypeSyntax p => p.Keyword.Text,
        _ => type.ToString(),
    };

    private static string SimpleName(string csharpType)
    {
        var t = csharpType.TrimEnd('?');
        var generic = t.IndexOf('<');
        if (generic >= 0)
        {
            t = t[..generic];
        }

        return t[(t.LastIndexOf('.') + 1)..];
    }

    /// <summary>True when a type in <paramref name="ns"/> can be written unqualified in the class: a using, or an enclosing namespace.</summary>
    private static bool IsNamespaceVisible(CompilationUnitSyntax root, ClassDeclarationSyntax cls, string ns)
    {
        if (string.IsNullOrEmpty(ns))
        {
            return true;
        }

        if (root.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(u => u.Alias is null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name?.ToString() == ns))
        {
            return true;
        }

        var enclosing = string.Join('.', cls.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));
        return enclosing == ns || enclosing.StartsWith(ns + ".", StringComparison.Ordinal);
    }

    /// <summary>Adds <c>using ns;</c> among the file's top-level usings (System first, then alphabetical), or at the top.</summary>
    private static string AddUsing(string text, CompilationUnitSyntax root, string ns, string newLine)
    {
        var line = $"using {ns};{newLine}";
        var usings = root.Usings.Where(u => u.Alias is null && u.StaticKeyword.IsKind(SyntaxKind.None) && !u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)).ToList();
        if (usings.Count == 0)
        {
            // Before the first member (namespace or type), after any header comments, followed by a blank line.
            var first = root.Members.FirstOrDefault();
            var at = first is null ? text.Length : first.GetFirstToken().SpanStart;
            var lineStart = text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
            return text.Insert(lineStart, line + newLine);
        }

        var next = usings.FirstOrDefault(u => CompareUsings(ns, u.Name!.ToString()) < 0);
        if (next is not null)
        {
            var start = next.GetFirstToken().SpanStart;
            var lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
            return text.Insert(lineStart, line);
        }

        var last = usings[^1];
        var end = text.IndexOf('\n', last.Span.End);
        return end < 0 ? text + newLine + line.TrimEnd('\r', '\n') : text.Insert(end + 1, line);
    }

    private static int CompareUsings(string a, string b)
    {
        static int Rank(string n) => n == "System" || n.StartsWith("System.", StringComparison.Ordinal) ? 0 : 1;
        var rank = Rank(a).CompareTo(Rank(b));
        return rank != 0 ? rank : string.CompareOrdinal(a, b);
    }

    /// <summary>Indentation of the class's members (from an existing member, else the class's own indent plus one level).</summary>
    private static string MemberIndent(ClassDeclarationSyntax cls, string text)
    {
        if (cls.Members.FirstOrDefault() is { } member)
        {
            return IndentAt(member.GetFirstToken().SpanStart, text);
        }

        var classIndent = ClassIndent(cls, text);
        return classIndent + (classIndent.Contains('\t') ? "\t" : "    ");
    }

    private static string ClassIndent(ClassDeclarationSyntax cls, string text) => IndentAt(cls.GetFirstToken().SpanStart, text);

    private static string IndentAt(int position, string text)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
        var prefix = text[lineStart..position];
        return string.IsNullOrWhiteSpace(prefix) ? prefix : new string(prefix.TakeWhile(char.IsWhiteSpace).ToArray());
    }

    private static int LineOf(int position, string text)
    {
        var line = 1;
        for (var i = 0; i < position && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static (string Text, Encoding Encoding) Read(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CodeBehindException($"Couldn't read {Path.GetFileName(path)}: {ex.Message}", ex);
        }

        Encoding encoding = bytes switch
        {
            [0xEF, 0xBB, 0xBF, ..] => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            [0xFF, 0xFE, ..] => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            [0xFE, 0xFF, ..] => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
        };
        var preamble = encoding.GetPreamble().Length;
        try
        {
            return (encoding.GetString(bytes, preamble, bytes.Length - preamble), encoding);
        }
        catch (DecoderFallbackException ex)
        {
            // Probably a legacy code page (Windows-1252): decoding it as UTF-8 and writing it back would destroy every
            // non-ASCII character in it.
            throw new CodeBehindException($"{Path.GetFileName(path)} isn't saved as UTF-8, so the designer won't change it. Save it as UTF-8 in your editor, then try again.", ex);
        }
    }

    private static void WriteAtomically(string path, string text, Encoding encoding)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, text, encoding);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // Nothing more to clean up.
            }

            throw new CodeBehindException($"Couldn't write {Path.GetFileName(path)}: {ex.Message}", ex);
        }
    }
}
