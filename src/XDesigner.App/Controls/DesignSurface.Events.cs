using Microsoft.UI.Xaml.Input;

namespace XDesigner.App.Controls;

/// <summary>
/// Double-click creates the default event handler (§8.3, §10). The confirmation is shown by the main window's
/// notification strip (D9), for handlers created here or on the Events tab.
/// </summary>
public sealed partial class DesignSurface
{
    private void AdornerLayer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Document is { } doc && HitTest(e.GetPosition(AdornerLayer)) is { } id)
        {
            _ = CancelDrag();
            e.Handled = true;
            _ = doc.CreateDefaultHandlerAsync(id);
        }
    }

    /// <summary>The faux title bar stands for the window itself (its default event is Opened).</summary>
    private void TitleBar_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Document is { } doc)
        {
            e.Handled = true;
            _ = doc.CreateDefaultHandlerAsync(doc.Xaml.Root.Id);
        }
    }
}

/// <summary>Right-click: the context menu (§8.3) for the control under the pointer.</summary>
public sealed partial class DesignSurface
{
    private void AdornerLayer_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (Document is not { } doc || HitTest(e.GetPosition(AdornerLayer)) is not { } id)
        {
            return;
        }

        if (!doc.Selection.IsSelected(id))
        {
            doc.Selection.Select(id); // right-clicking outside the selection acts on what was clicked
        }

        e.Handled = true;
        DesignCommands.BuildMenu(doc, id).ShowAt(AdornerLayer, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(AdornerLayer) });
    }
}
