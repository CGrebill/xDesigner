using XDesigner.Core.Toolbox;

namespace XDesigner.App.Services;

/// <summary>
/// The toolbox item being dragged right now. Drag and drop stays inside this app, so the surface reads the
/// item from here synchronously during DragOver instead of awaiting the data package.
/// </summary>
public static class ToolboxDrag
{
    /// <summary>Text format put on the data package (identifies designer drags).</summary>
    public const string DataPrefix = "xdesigner/toolbox:";

    public static ToolboxItem? Current { get; set; }
}
