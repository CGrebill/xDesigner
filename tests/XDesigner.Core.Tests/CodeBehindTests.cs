using System.Text;
using XDesigner.Core.CodeGen;
using XDesigner.Core.Editing;
using XDesigner.Core.Metadata;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;
using Microsoft.CodeAnalysis.CSharp;

namespace XDesigner.Core.Tests;

public sealed class CodeBehindTests : IDisposable
{
    private static readonly HandlerSignature Click = new("object?", "RoutedEventArgs", "Avalonia.Interactivity", ["EventArgs"]);
    private static readonly HandlerSignature TextChanged = new("object?", "TextChangedEventArgs", "Avalonia.Controls", ["RoutedEventArgs", "EventArgs"]);

    private readonly string _dir = Directory.CreateTempSubdirectory("xdesigner-codebehind-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string FileScoped =
        "using Avalonia.Controls;\r\n" +
        "using Avalonia.Interactivity;\r\n" +
        "\r\n" +
        "namespace App.Views;\r\n" +
        "\r\n" +
        "public partial class MainWindow : Window\r\n" +
        "{\r\n" +
        "    public MainWindow()\r\n" +
        "    {\r\n" +
        "        InitializeComponent();\r\n" +
        "    }\r\n" +
        "}\r\n";

    private string Write(string name, string text, bool bom = false)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, new UTF8Encoding(bom));
        return path;
    }

    [Fact]
    public void AddsMethodBeforeClosingBrace_WithBlankLineAndFileStyle()
    {
        var path = Write("MainWindow.axaml.cs", FileScoped);

        var result = CodeBehindWriter.EnsureHandler(path, "App.Views.MainWindow", "button1_Click", Click);

        Assert.True(result.Created);
        Assert.Equal(13, result.Line);
        var text = File.ReadAllText(path);
        Assert.Equal(FileScoped.Replace("    }\r\n}\r\n",
            "    }\r\n\r\n    private void button1_Click(object? sender, RoutedEventArgs e)\r\n    {\r\n    }\r\n}\r\n", StringComparison.Ordinal), text);
        Assert.Equal("    private void button1_Click(object? sender, RoutedEventArgs e)", text.Split("\r\n")[result.Line - 1]);
    }

    [Fact]
    public void ReusesACompatibleMethod_AndSkipsIncompatibleNames()
    {
        var path = Write("A.axaml.cs", FileScoped);
        CodeBehindWriter.EnsureHandler(path, "App.Views.MainWindow", "ok_Click", Click);
        var before = File.ReadAllText(path);

        var again = CodeBehindWriter.EnsureHandler(path, "App.Views.MainWindow", "ok_Click", Click);
        Assert.False(again.Created);
        Assert.Equal(before, File.ReadAllText(path));

        // Same name, but TextChangedEventArgs can't be passed to a RoutedEventArgs... it can (base type), so reuse:
        Assert.False(CodeBehindWriter.EnsureHandler(path, "App.Views.MainWindow", "ok_Click", TextChanged).Created);

        // A name used by something that isn't a fitting method gets a suffix.
        var pointer = new HandlerSignature("object?", "PointerPressedEventArgs", "Avalonia.Input", ["PointerEventArgs", "RoutedEventArgs", "EventArgs"]);
        Directory.CreateDirectory(Path.Combine(_dir, "other")); // not next to A, which declares the same partial class
        var path2 = Write(Path.Combine("other", "B.axaml.cs"), FileScoped.Replace("    public MainWindow()", "    private int ok_Click;\r\n\r\n    public MainWindow()", StringComparison.Ordinal));
        var suffixed = CodeBehindWriter.EnsureHandler(path2, "App.Views.MainWindow", "ok_Click", pointer);
        Assert.Equal("ok_Click_1", suffixed.MethodName);
        Assert.Contains("using Avalonia.Input;\r\nusing Avalonia.Interactivity;", File.ReadAllText(path2));
    }

    [Fact]
    public void BlockNamespace_LfEndings_Bom_AndMissingUsingsArePreserved()
    {
        const string source = "// header\nnamespace App\n{\n    public partial class Page : Avalonia.Controls.UserControl\n    {\n    }\n}\n";
        var path = Write("Page.axaml.cs", source, bom: true);

        var result = CodeBehindWriter.EnsureHandler(path, "App.Page", "Page_Loaded", Click);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.DoesNotContain("\r", text);
        Assert.Equal(
            "// header\nusing Avalonia.Interactivity;\n\nnamespace App\n{\n    public partial class Page : Avalonia.Controls.UserControl\n    {\n" +
            "        private void Page_Loaded(object? sender, RoutedEventArgs e)\n        {\n        }\n    }\n}\n", text);
        Assert.Equal(8, result.Line);
    }

    [Fact]
    public void EnclosingNamespace_NeedsNoUsing()
    {
        var path = Write("C.axaml.cs", "namespace Avalonia.Interactivity.Demo;\n\npublic partial class C\n{\n}\n");
        CodeBehindWriter.EnsureHandler(path, "Avalonia.Interactivity.Demo.C", "C_Click", Click);
        Assert.DoesNotContain("using", File.ReadAllText(path));
    }

    [Fact]
    public void MissingClass_Throws()
    {
        var path = Write("D.axaml.cs", FileScoped);
        var ex = Assert.Throws<CodeBehindException>(() => CodeBehindWriter.EnsureHandler(path, "App.Views.Other", "x_Click", Click));
        Assert.Contains("App.Views.Other", ex.Message);
    }

    [Fact]
    public void Result_ParsesWithoutErrors()
    {
        var path = Write("E.axaml.cs", FileScoped);
        CodeBehindWriter.EnsureHandler(path, "App.Views.MainWindow", "a_Click", Click);
        CodeBehindWriter.EnsureHandler(path, "App.Views.MainWindow", "b_TextChanged", TextChanged);

        var diagnostics = CSharpSyntaxTree.ParseText(File.ReadAllText(path), cancellationToken: TestContext.Current.CancellationToken).GetDiagnostics(TestContext.Current.CancellationToken);
        Assert.Empty(diagnostics);
        Assert.Equal(["a_Click", "b_TextChanged"], CodeBehindWriter.CompatibleMethods(path, "App.Views.MainWindow", TextChanged).Select(m => m.Name));
        Assert.Equal(["a_Click"], CodeBehindWriter.CompatibleMethods(path, "App.Views.MainWindow", Click).Select(m => m.Name).Where(n => n == "a_Click"));
    }

    [Fact]
    public void AHandlerInAnotherPartOfTheClass_IsReused_NotDuplicated()
    {
        var path = Write("MainWindow.axaml.cs", FileScoped);
        var handlers = Write("MainWindow.Handlers.cs",
            "using Avalonia.Interactivity;\r\n\r\nnamespace App.Views;\r\n\r\npublic partial class MainWindow\r\n{\r\n" +
            "    private void ok_Click(object? sender, RoutedEventArgs e)\r\n    {\r\n    }\r\n\r\n    private int busy_Click;\r\n}\r\n");

        var reused = CodeBehindWriter.EnsureHandler(path, "App.Views.MainWindow", "ok_Click", Click);
        Assert.Equal((false, handlers, 7), (reused.Created, reused.FilePath, reused.Line));
        Assert.Equal(FileScoped, File.ReadAllText(path));

        // A name taken by a member in the other part, or by a control's x:Name field, gets a suffix.
        Assert.Equal("busy_Click_1", CodeBehindWriter.EnsureHandler(path, "App.Views.MainWindow", "busy_Click", Click).MethodName);
        Assert.Equal("ok_1", CodeBehindWriter.EnsureHandler(path, "App.Views.MainWindow", "ok", Click, new HashSet<string> { "ok" }).MethodName);
    }

    [Fact]
    public void AFileThatIsNotUtf8_IsRefused_AndLeftAlone()
    {
        var path = Path.Combine(_dir, "MainWindow.axaml.cs");
        var original = Encoding.Latin1.GetBytes(FileScoped.Replace("InitializeComponent();", "InitializeComponent(); // Größe", StringComparison.Ordinal));
        File.WriteAllBytes(path, original);

        var ex = Assert.Throws<CodeBehindException>(() => CodeBehindWriter.EnsureHandler(path, "App.Views.MainWindow", "ok_Click", Click));

        Assert.Contains("UTF-8", ex.Message);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void AFileWithSyntaxErrors_IsRefused_AndLeftAlone()
    {
        var broken = FileScoped.Replace("    }\r\n}\r\n", "    }\r\n", StringComparison.Ordinal); // the class's closing brace is missing
        var path = Write("MainWindow.axaml.cs", broken);

        var ex = Assert.Throws<CodeBehindException>(() => CodeBehindWriter.EnsureHandler(path, "App.Views.MainWindow", "ok_Click", Click));

        Assert.Contains("syntax error", ex.Message);
        Assert.Equal(broken, File.ReadAllText(path));
    }

    // ---- DesignerActions.ConnectHandler ----

    private static readonly ControlCatalog Catalog = new([
        new ControlTypeInfo("Avalonia.Controls.Window", "Window", ControlTypeInfo.AvaloniaXmlNamespace, "Avalonia.Controls", "Avalonia.Controls", true, true, ContentModel.SingleContent, "Content", "Opened", []),
        new ControlTypeInfo("Avalonia.Controls.Button", "Button", ControlTypeInfo.AvaloniaXmlNamespace, "Avalonia.Controls", "Avalonia.Controls", true, true, ContentModel.SingleContent, "Content", "Click", [], ["Click"]),
        new ControlTypeInfo("Avalonia.Controls.StackPanel", "StackPanel", ControlTypeInfo.AvaloniaXmlNamespace, "Avalonia.Controls", "Avalonia.Controls", true, true, ContentModel.Children, "Children", null, []),
    ]);

    private static readonly EventInfoDto ClickEvent = new("Click", true, "object?", "RoutedEventArgs", "Avalonia.Interactivity", ["EventArgs"]);

    private const string Xaml =
        "<Window xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" x:Class=\"App.Views.MainWindow\">\r\n" +
        "  <StackPanel>\r\n" +
        "    <Button Content=\"A\" />\r\n" +
        "    <Button x:Name=\"ok\" Content=\"OK\" />\r\n" +
        "  </StackPanel>\r\n" +
        "</Window>\r\n";

    [Fact]
    public void Connect_NamesTheControl_WritesCodeFirst_AndUndoKeepsTheMethod()
    {
        var xamlPath = Write("MainWindow.axaml", Xaml);
        var cs = Write("MainWindow.axaml.cs", FileScoped);
        var doc = XamlDocument.Parse(Xaml);
        var undo = new UndoManager(doc);
        var actions = new DesignerActions(doc, undo);
        var unnamed = doc.AllNodes.First(n => n.TypeName == "Button").Id;

        var outcome = actions.ConnectHandler([unnamed], ClickEvent, null, xamlPath, Catalog);

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal("button1", outcome.ControlName);
        Assert.Equal("button1_Click", outcome.Handler!.MethodName);
        Assert.Contains("<Button x:Name=\"button1\" Content=\"A\" Click=\"button1_Click\" />", doc.ToXaml());
        Assert.Contains("private void button1_Click(object? sender, RoutedEventArgs e)", File.ReadAllText(cs));
        Assert.Equal($"Created handler button1_Click for Button.Click (button1) in MainWindow.axaml.cs, line {outcome.Handler.Line}.", outcome.Message);
        Assert.Equal("Connect Click to button1_Click", undo.UndoDescription);

        undo.Undo();
        Assert.Equal(Xaml, doc.ToXaml());
        Assert.Contains("button1_Click", File.ReadAllText(cs)); // D10: the method stays

        // Connecting again reuses the method.
        var again = actions.ConnectHandler([unnamed], ClickEvent, null, xamlPath, Catalog);
        Assert.False(again.Handler!.Created);
        Assert.StartsWith("Connected Button.Click (button1) to the existing method button1_Click", again.Message);
    }

    [Fact]
    public void Connect_CreatesAMissingCodeBehind_AndRejectsFilesWithoutXClass()
    {
        var xamlPath = Write("Other.axaml", Xaml);
        var doc = XamlDocument.Parse(Xaml);
        var actions = new DesignerActions(doc, new UndoManager(doc));
        var ok = doc.AllNodes.Single(n => n.Name == "ok").Id;

        var outcome = actions.ConnectHandler([ok], ClickEvent, null, xamlPath, Catalog);
        Assert.True(outcome.CreatedCodeBehind);
        var text = File.ReadAllText(xamlPath + ".cs");
        Assert.Contains("public partial class MainWindow : Window", text);
        Assert.Contains("private void ok_Click(object? sender, RoutedEventArgs e)", text);
        Assert.Empty(CSharpSyntaxTree.ParseText(text, cancellationToken: TestContext.Current.CancellationToken).GetDiagnostics(TestContext.Current.CancellationToken));

        var noClass = XamlDocument.Parse(Xaml.Replace(" x:Class=\"App.Views.MainWindow\"", "", StringComparison.Ordinal));
        var failed = new DesignerActions(noClass, new UndoManager(noClass))
            .ConnectHandler([noClass.AllNodes.Single(n => n.Name == "ok").Id], ClickEvent, null, xamlPath, Catalog);
        Assert.False(failed.Success);
        Assert.Contains("x:Class", failed.Message);
    }

    [Fact]
    public void Disconnect_RemovesOnlyTheAttribute()
    {
        var xamlPath = Write("Main.axaml", Xaml);
        Write("Main.axaml.cs", FileScoped);
        var doc = XamlDocument.Parse(Xaml);
        var actions = new DesignerActions(doc, new UndoManager(doc));
        var ok = doc.AllNodes.Single(n => n.Name == "ok").Id;
        actions.ConnectHandler([ok], ClickEvent, "Shared", xamlPath, Catalog);

        Assert.True(actions.DisconnectHandler([ok], "Click"));
        Assert.Equal(Xaml, doc.ToXaml());
        Assert.Contains("private void Shared(", File.ReadAllText(xamlPath + ".cs"));
        Assert.False(actions.DisconnectHandler([ok], "Click"));
    }
}
