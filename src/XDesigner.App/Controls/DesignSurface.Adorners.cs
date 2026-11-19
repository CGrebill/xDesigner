using XDesigner.Core.Editing;
using XDesigner.Protocol.Rendering;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI.Core;

namespace XDesigner.App.Controls;

/// <summary>
/// Adorner layer and selection input (§8.1, §8.3). Element bounds come from the render's layout tree in
/// frame DIPs; on screen they are multiplied by the display factor (the current zoom).
/// </summary>
public sealed partial class DesignSurface
{
    private const double HandleSize = 7;

    private int? _hoverId;

    /// <summary>WinUI units per frame DIP (the zoom, once the matching render has arrived).</summary>
    private double DisplayFactor =>
        _shown is { } frame && FrameImage.Width > 0 ? FrameImage.Width / (frame.PixelWidth / frame.Scale) : 1;

    private static Brush AccentBrush => (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    private void AdornerLayer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_hoverId is not null)
        {
            _hoverId = null;
            RedrawAdorners();
        }
    }

    private void TitleBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Document is { } doc)
        {
            Focus(FocusState.Pointer);
            doc.Selection.Select(doc.Xaml.Root.Id);
            e.Handled = true;
        }
    }

    private void Surface_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Document is not { } doc)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape && CancelDrag())
        {
            e.Handled = true;
            return;
        }

        if (IsDragging)
        {
            return;
        }

        if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
        {
            var step = IsShiftDown() ? 10 : 1;
            var (dx, dy) = e.Key switch
            {
                VirtualKey.Left => (-step, 0),
                VirtualKey.Right => (step, 0),
                VirtualKey.Up => (0, -step),
                _ => (0, step),
            };
            doc.Nudge(dx, dy);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.A && IsKeyDown(VirtualKey.Control))
        {
            doc.SelectAllSiblings();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Delete)
        {
            doc.DeleteSelection();
            e.Handled = true;
            return;
        }

        int? target = e.Key switch
        {
            VirtualKey.Escape => SelectionNavigator.Parent(doc.Xaml, doc.Selection),
            VirtualKey.Tab => SelectionNavigator.Sibling(doc.Xaml, doc.Selection, forward: !IsShiftDown()),
            _ => null,
        };

        if (e.Key is VirtualKey.Escape or VirtualKey.Tab)
        {
            e.Handled = true; // keep Tab on the surface instead of moving focus
        }

        if (target is { } id)
        {
            doc.Selection.Select(id);
        }
    }

    private static bool IsShiftDown() => IsKeyDown(VirtualKey.Shift);

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>Design id of the element under a point on the adorner layer (only elements that exist in the document).</summary>
    private int? HitTest(Windows.Foundation.Point position)
    {
        if (Document is not { } doc)
        {
            return null;
        }

        var factor = DisplayFactor;
        return doc.Layout.HitTest(position.X / factor, position.Y / factor, e => doc.Xaml.FindById(e.DesignId) is not null)?.DesignId;
    }

    /// <summary>Redraws hover and selection adorners from the current layout, selection and zoom.</summary>
    private void RedrawAdorners()
    {
        AdornerLayer.Children.Clear();
        if (Document is not { } doc || _shown is null)
        {
            return;
        }

        AdornerLayer.Width = FrameImage.Width;
        AdornerLayer.Height = FrameImage.Height;
        var factor = DisplayFactor;

        if (!IsDragging && _hoverId is { } hover && !doc.Selection.IsSelected(hover) && doc.Layout.Find(hover) is { } hovered)
        {
            AdornerLayer.Children.Add(Outline(hovered.Bounds, factor, thickness: 1, dashed: true));
        }

        DrawDropIndicator(factor);
        DrawDragFeedback(factor);
        if (_drag is DragKind.Move or DragKind.Resize)
        {
            return; // the drag feedback replaces the selection adorners until release
        }

        foreach (var id in doc.Selection.Selected)
        {
            if (doc.Layout.Find(id) is not { } element)
            {
                continue; // selected in the outline but not rendered (collapsed, or not a visual)
            }

            var isPrimary = id == doc.Selection.Primary;
            AdornerLayer.Children.Add(Outline(element.Bounds, factor, thickness: isPrimary ? 2 : 1, dashed: !isPrimary));
            if (isPrimary)
            {
                AddHandles(element.Bounds, factor);
                AddNameTag(element, factor);
            }
        }
    }

    private static Rectangle Outline(RectDto bounds, double factor, double thickness, bool dashed)
    {
        var rect = new Rectangle
        {
            Width = Math.Max(1, bounds.Width * factor + thickness),
            Height = Math.Max(1, bounds.Height * factor + thickness),
            Stroke = AccentBrush,
            StrokeThickness = thickness,
            IsHitTestVisible = false,
        };
        if (dashed)
        {
            rect.StrokeDashArray = [3, 2];
        }

        Canvas.SetLeft(rect, bounds.X * factor - thickness / 2);
        Canvas.SetTop(rect, bounds.Y * factor - thickness / 2);
        return rect;
    }

    /// <summary>Eight resize handles around the primary selection.</summary>
    private void AddHandles(RectDto bounds, double factor)
    {
        double[] xs = [bounds.X * factor, (bounds.X + bounds.Width / 2) * factor, bounds.Right * factor];
        double[] ys = [bounds.Y * factor, (bounds.Y + bounds.Height / 2) * factor, bounds.Bottom * factor];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                if (i == 1 && j == 1)
                {
                    continue;
                }

                var handle = new Rectangle
                {
                    Width = HandleSize,
                    Height = HandleSize,
                    Fill = new SolidColorBrush(Colors.White),
                    Stroke = AccentBrush,
                    StrokeThickness = 1,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(handle, xs[i] - HandleSize / 2);
                Canvas.SetTop(handle, ys[j] - HandleSize / 2);
                AdornerLayer.Children.Add(handle);
            }
        }
    }

    /// <summary>A small "button1 · Button" tag above the primary selection.</summary>
    private void AddNameTag(ElementLayout element, double factor)
    {
        var node = Document?.Xaml.FindById(element.DesignId);
        if (node is null)
        {
            return;
        }

        var tag = new Border
        {
            Background = AccentBrush,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 1),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = node.Name is null ? node.TypeName : $"{node.Name} · {node.TypeName}",
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"],
            },
        };
        Canvas.SetLeft(tag, element.Bounds.X * factor);
        // Above the element, or inside its top edge when it starts at the top of the frame.
        Canvas.SetTop(tag, element.Bounds.Y * factor >= 20 ? element.Bounds.Y * factor - 20 : element.Bounds.Y * factor + 2);
        AdornerLayer.Children.Add(tag);
    }
}
