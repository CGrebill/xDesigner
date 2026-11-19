namespace XDesigner.App.ViewModels;

/// <summary>
/// One line of the Output, Build or Events log. A class (reference identity), not a string: WinUI's ListView maps items
/// to rows by equality, so repeated identical lines (blank lines, "0 Error(s)" in every build) made its UI Automation
/// tree loop forever, and anything walking it (screen readers, accessibility tools) hung the app.
/// </summary>
public sealed class LogLine(string text)
{
    public string Text { get; } = text;

    public override string ToString() => Text;
}
