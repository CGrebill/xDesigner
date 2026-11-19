using XDesigner.App.ViewModels;
using XDesigner.Core.Layout;
using XDesigner.Core.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace XDesigner.App.Controls;

/// <summary>
/// Outline drag and drop (§9.6): drop on the top or bottom quarter of a row to put the control before or after it, on
/// the middle to put it inside (when that control can hold it). The move is the same command as a surface move, so
/// layout attributes are fixed up and undo restores the file exactly. Right-click opens the context menu.
/// </summary>
public sealed partial class DocumentOutline
{
    private OutlineNodeViewModel? _dragged;
    private ScrollViewer? _scroller;

    private enum DropMode
    {
        Before,
        After,
        Into,
    }

    private (OutlineNodeViewModel Node, TreeViewItem Row, Windows.Foundation.Point At)? _press;

    /// <summary>
    /// The drag is started by hand (<see cref="UIElement.StartDragAsync"/>) once a pressed row moves a few pixels. Not the
    /// TreeView's own item drag: on drop it reorders its item collections by itself, putting the outline out of step with
    /// the file. Not <c>CanDrag</c> on the row either: that stops clicks from selecting rows. Hooked up in the constructor
    /// with handledEventsToo, because the row handles the press itself (selection).
    /// </summary>
    private void Tree_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _press = null;
        var point = e.GetCurrentPoint(Tree);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        for (var element = e.OriginalSource as DependencyObject; element is not null && element != Tree; element = VisualTreeHelper.GetParent(element))
        {
            if (element is Button)
            {
                return; // the eye icon
            }

            if (element is TreeViewItem row && Tree.ItemFromContainer(row) is OutlineNodeViewModel { Parent: not null } node)
            {
                _press = (node, row, point.Position); // the root stays where it is
                return;
            }
        }
    }

    private async void Tree_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_press is not { } press)
        {
            return;
        }

        var point = e.GetCurrentPoint(Tree);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _press = null;
            return;
        }

        if (Math.Abs(point.Position.X - press.At.X) < 6 && Math.Abs(point.Position.Y - press.At.Y) < 6)
        {
            return;
        }

        _press = null;
        _dragged = press.Node;
        void Starting(UIElement s, DragStartingEventArgs args)
        {
            args.AllowedOperations = DataPackageOperation.Move;
            args.Data.RequestedOperation = DataPackageOperation.Move;
            args.Data.SetText(press.Node.Label);
        }

        press.Row.DragStarting += Starting;
        try
        {
            await press.Row.StartDragAsync(e.GetCurrentPoint(press.Row));
        }
        finally
        {
            press.Row.DragStarting -= Starting;
            _dragged = null;
        }
    }

    private void Tree_PointerReleased(object sender, PointerRoutedEventArgs e) => _press = null;

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if ((child as ScrollViewer ?? FindScroller(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void Tree_DragOver(object sender, DragEventArgs e)
    {
        ScrollNearEdge(e);
        if (Plan(e) is { } plan)
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = plan.Caption;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.Caption = "";
        }

        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = false;
        e.Handled = true;
    }

    /// <summary>Within a row's height of the top or bottom of the tree, scroll a little each time the pointer moves or rests.</summary>
    private void ScrollNearEdge(DragEventArgs e)
    {
        const double edge = 24;
        if ((_scroller ??= FindScroller(Tree)) is not { } scroller)
        {
            return;
        }

        var y = e.GetPosition(Tree).Y;
        var step = y < edge ? -8 : y > Tree.ActualHeight - edge ? 8 : 0;
        if (step != 0)
        {
            scroller.ChangeView(null, Math.Clamp(scroller.VerticalOffset + step, 0, scroller.ScrollableHeight), null, disableAnimation: true);
        }
    }

    private void Tree_Drop(object sender, DragEventArgs e)
    {
        if (Document is not { } doc || _dragged is not { } dragged || Plan(e) is not { } plan)
        {
            return;
        }

        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.Move;

        // Move once the drop has finished: the move rebuilds the outline rows, and removing the row the drag started
        // from while the drag is still completing leaves the window ignoring clicks and keys.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (doc.Xaml.FindById(dragged.Id) is null)
            {
                return;
            }

            if (doc.Actions.MoveToContainer(dragged.Id, plan.ContainerId, plan.Index, doc.Layout, doc.Catalog) is { } problem)
            {
                DesignCommands.Host?.ShowMessage(problem);
            }
            else
            {
                doc.Selection.Select(dragged.Id);
            }
        });
    }

    /// <summary>Where a drop at the pointer would put the dragged control, or null when it can't go there.</summary>
    private (int ContainerId, int Index, string Caption)? Plan(DragEventArgs e)
    {
        if (Document is not { } doc || _dragged is not { } dragged || RowAt(e) is not { } row
            || doc.Xaml.FindById(row.Node.Id) is not { } target || doc.Xaml.FindById(dragged.Id) is not { } moving)
        {
            return null;
        }

        if (target.AncestorsAndSelf().Any(a => a.Id == moving.Id))
        {
            return null; // into itself or its own children
        }

        var y = e.GetPosition(row.Container).Y / Math.Max(1, row.Container.ActualHeight);
        var canHold = doc.Catalog.Resolve(target) is { } type && DropPlanner.Accepts(target, type, doc.Actions.WithDescendants([moving.Id]));
        var mode = y < 0.25 ? DropMode.Before : y > 0.75 ? DropMode.After : canHold ? DropMode.Into : y < 0.5 ? DropMode.Before : DropMode.After;

        if (mode == DropMode.Into)
        {
            return (target.Id, int.MaxValue, $"Move {moving.DisplayName} into {target.DisplayName}");
        }

        if (target.Parent is not { } parent)
        {
            return null; // nothing goes beside the root
        }

        var siblings = target.Element.Parent!.Elements().Where(RenderCopyBuilder.IsObjectElement).ToList();
        var index = siblings.IndexOf(target.Element) + (mode == DropMode.After ? 1 : 0);
        var caption = $"Move {moving.DisplayName} {(mode == DropMode.After ? "after" : "before")} {target.DisplayName}";
        return (parent.Id, index, caption);
    }

    /// <summary>The outline row under the pointer, with its container (for the position within the row).</summary>
    private (OutlineNodeViewModel Node, TreeViewItem Container)? RowAt(DragEventArgs e)
    {
        var point = e.GetPosition(null);
        foreach (var element in VisualTreeHelper.FindElementsInHostCoordinates(point, Tree))
        {
            if (element is TreeViewItem item && Tree.ItemFromContainer(item) is OutlineNodeViewModel node)
            {
                return (node, item);
            }
        }

        return null;
    }

    /// <summary>Right-click a row: select it (unless it's already part of the selection) and open the context menu.</summary>
    private void Tree_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Document is not { } doc)
        {
            return;
        }

        for (var element = e.OriginalSource as DependencyObject; element is not null && element != Tree; element = VisualTreeHelper.GetParent(element))
        {
            if (element is TreeViewItem item && Tree.ItemFromContainer(item) is OutlineNodeViewModel node)
            {
                if (!doc.Selection.IsSelected(node.Id))
                {
                    doc.Selection.Select(node.Id);
                }

                e.Handled = true;
                DesignCommands.BuildMenu(doc, node.Id).ShowAt(item, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(item) });
                return;
            }
        }
    }
}
