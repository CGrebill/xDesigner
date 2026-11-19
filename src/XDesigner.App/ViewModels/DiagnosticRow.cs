using XDesigner.Core.Project;

namespace XDesigner.App.ViewModels;

/// <summary>One row of Output ▸ Error List: a build error or warning, and where it is.</summary>
public sealed class DiagnosticRow(BuildDiagnostic diagnostic)
{
    public BuildDiagnostic Diagnostic { get; } = diagnostic;

    public string Glyph => Diagnostic.IsError ? "" : "";

    public string Code => Diagnostic.Code;

    public string Message => Diagnostic.Message;

    /// <summary><c>MainWindow.axaml.cs, line 12</c>, or nothing for a diagnostic without a file.</summary>
    public string Location => Diagnostic.File is { } file
        ? $"{Path.GetFileName(file)}{(Diagnostic.Line is { } line ? $", line {line}" : "")}"
        : "";

    /// <summary>Screen readers read the whole row (lists of records would read their type name).</summary>
    public override string ToString() => $"{(Diagnostic.IsError ? "Error" : "Warning")} {Code}: {Message} {Location}";
}
