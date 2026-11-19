using XDesigner.Core.Editing;
using XDesigner.Core.Layout;
using XDesigner.Protocol.Rendering;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;

namespace XDesigner.App.Controls;

/// <summary>
/// Pointer interaction on the design surface (§8.3): click/Ctrl/Shift selection, drag to move (with snaplines and
/// reparenting), drag handles to resize, rubber-band selection, cursor feedback. Nothing is written to the document
/// until the mouse is released, and then as one undoable step; no re-render happens while dragging.
/// </summary>
public sealed partial class DesignSurface
{
    /// <summary>Pointer travel (screen units) before a press becomes a drag.</summary>
    private const double DragThreshold = 4;

    private DragKind _drag;
    private Point _pressAt;
    private Point _pointerAt;
    private int? _pressHit;
    private bool _pressHitWasSelected;
    private ResizeEdges _resizeEdges;
    private int? _containerForBand;
    private Dictionary<int, RectDto> _moveStarts = [];
    private RectDto? _proposed;
    private IReadOnlyList<SnapLine> _snapLines = [];
    private HashSet<int> _bandHits = [];

    private enum DragKind
    {
        None,
        PendingMove,
        PendingResize,
        PendingBand,
        Move,
        Resize,
        Band,
    }

    private bool IsDragging => _drag is DragKind.Move or DragKind.Resize or DragKind.Band;

    private void AdornerLayer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(AdornerLayer);
        if (!point.Properties.IsLeftButtonPressed || Document is not { } doc)
        {
            return;
        }

        Focus(FocusState.Pointer);
        _pressAt = _pointerAt = point.Position;

        // A resize handle of the primary selection wins over whatever is underneath it.
        if (HandleAt(point.Position) is { } edges && doc.Selection.Primary is { } primary && doc.Layout.Find(primary) is { } bounds)
        {
            _drag = DragKind.PendingResize;
            _resizeEdges = edges;
            _moveStarts = new() { [primary] = bounds.Bounds };
            AdornerLayer.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        if (HitTest(point.Position) is not { } id)
        {
            return;
        }

        var modifiers = e.KeyModifiers;
        if (modifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            doc.Selection.Toggle(id);
            e.Handled = true;
            return;
        }

        if (modifiers.HasFlag(VirtualKeyModifiers.Shift))
        {
            doc.Selection.Add(id);
            e.Handled = true;
            return;
        }

        _pressHit = id;
        _pressHitWasSelected = doc.Selection.IsSelected(id);
        if (!_pressHitWasSelected)
        {
            doc.Selection.Select(id);
        }

        // Dragging the form's background, or a panel that wasn't selected yet, draws a selection box inside it;
        // dragging anything else (or an already selected panel) moves the selection.
        var node = doc.Xaml.FindById(id);
        var isContainer = node is not null && doc.Catalog.Resolve(node)?.Content is Protocol.Metadata.ContentModel.Children;
        if (node?.Parent is null || (isContainer && !_pressHitWasSelected))
        {
            _drag = DragKind.PendingBand;
            _containerForBand = id;
        }
        else
        {
            _drag = DragKind.PendingMove;
            _moveStarts = doc.Selection.Selected
                .Where(s => s != doc.Xaml.Root.Id)
                .Select(s => (Id: s, Layout: doc.Layout.Find(s)))
                .Where(s => s.Layout is not null)
                .ToDictionary(s => s.Id, s => s.Layout!.Bounds);
        }

        AdornerLayer.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void AdornerLayer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(AdornerLayer).Position;
        if (_drag == DragKind.None)
        {
            UpdateHover(position);
            return;
        }

        _pointerAt = position;
        if (_drag is DragKind.PendingMove or DragKind.PendingResize or DragKind.PendingBand)
        {
            if (Math.Abs(position.X - _pressAt.X) < DragThreshold && Math.Abs(position.Y - _pressAt.Y) < DragThreshold)
            {
                return;
            }

            _drag = _drag switch
            {
                DragKind.PendingMove when _moveStarts.Count > 0 => DragKind.Move,
                DragKind.PendingResize => DragKind.Resize,
                DragKind.PendingBand => DragKind.Band,
                _ => DragKind.None,
            };
            _hoverId = null;
        }

        var altDown = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu);
        switch (_drag)
        {
            case DragKind.Move:
                UpdateMove(altDown);
                break;
            case DragKind.Resize:
                UpdateResize(altDown);
                break;
            case DragKind.Band:
                UpdateBand();
                break;
        }

        RedrawAdorners();
    }

    private void AdornerLayer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (Document is { } doc)
        {
            var altDown = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu);
            switch (_drag)
            {
                case DragKind.PendingMove when _pressHitWasSelected && _pressHit is { } hit && doc.Selection.Selected.Count > 1:
                    doc.Selection.Select(hit); // a click (not a drag) on one of several selected controls selects just it
                    break;
                case DragKind.Move:
                    CommitMove(doc, altDown);
                    break;
                case DragKind.Resize when _proposed is { } rect && doc.Selection.Primary is { } id:
                    doc.Resize(id, rect, _resizeEdges);
                    break;
                case DragKind.Band:
                    if (_bandHits.Count > 0)
                    {
                        doc.Selection.Set(_bandHits);
                    }

                    break;
            }
        }

        EndDrag();
        AdornerLayer.ReleasePointerCaptures();
    }

    private void AdornerLayer_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndDrag();

    /// <summary>Esc during a drag puts everything back.</summary>
    private bool CancelDrag()
    {
        if (_drag == DragKind.None)
        {
            return false;
        }

        EndDrag();
        AdornerLayer.ReleasePointerCaptures();
        return true;
    }

    private void EndDrag()
    {
        _drag = DragKind.None;
        _pressHit = null;
        _containerForBand = null;
        _moveStarts = [];
        _proposed = null;
        _snapLines = [];
        _bandHits = [];
        _dropPlan = null;
        RedrawAdorners();
    }

    private void UpdateHover(Point position)
    {
        var hit = HitTest(position);
        if (hit != _hoverId)
        {
            _hoverId = hit;
            RedrawAdorners();
        }

        ProtectedCursor = HandleAt(position) is { } edges
            ? InputSystemCursor.Create(CursorFor(edges))
            : hit is { } id && Document?.Selection.IsSelected(id) == true && Document.Xaml.FindById(id)?.Parent is not null
                ? InputSystemCursor.Create(InputSystemCursorShape.SizeAll)
                : null;
    }

    // ---- Move ----

    private (double Dx, double Dy) FrameDelta()
    {
        var factor = DisplayFactor;
        return ((_pointerAt.X - _pressAt.X) / factor, (_pointerAt.Y - _pressAt.Y) / factor);
    }

    private void UpdateMove(bool altDown)
    {
        if (Document is not { } doc || doc.Selection.Primary is not { } primary || !_moveStarts.TryGetValue(primary, out var start))
        {
            return;
        }

        var (dx, dy) = FrameDelta();
        var proposed = start with { X = start.X + dx, Y = start.Y + dy };

        // Snap the primary element against its siblings and container; the rest of the selection follows.
        var snapped = altDown ? new SnapResult(proposed, []) : SnapAgainstSiblings(doc, primary, proposed, ResizeEdges.None);
        _proposed = snapped.Bounds;
        _snapLines = snapped.Lines;

        // Moving one element may drop it into another container (Alt keeps it in its own).
        _dropPlan = null;
        if (_moveStarts.Count == 1 && !altDown)
        {
            var factor = DisplayFactor;
            var plan = DropPlanner.Plan(doc.Xaml, doc.Catalog, doc.Layout, _pointerAt.X / factor, _pointerAt.Y / factor, out _,
                doc.Actions.WithDescendants([primary]), _proposed.X, _proposed.Y);
            var parent = doc.Xaml.FindById(primary)?.Parent?.Id;
            if (plan is not null && (plan.ContainerId != parent || plan.Indicator.Kind == DropIndicatorKind.InsertionLine))
            {
                _dropPlan = plan;
                _snapLines = plan.ContainerId != parent ? [] : _snapLines;
            }
        }
    }

    private void CommitMove(ViewModels.DocumentViewModel doc, bool altDown)
    {
        if (_proposed is not { } proposed || doc.Selection.Primary is not { } primary || !_moveStarts.TryGetValue(primary, out var start))
        {
            return;
        }

        if (_dropPlan is { } plan && !altDown)
        {
            doc.MoveTo(primary, plan);
        }
        else
        {
            doc.MoveBy(_moveStarts.Keys.ToList(), proposed.X - start.X, proposed.Y - start.Y);
        }
    }

    // ---- Resize ----

    private void UpdateResize(bool altDown)
    {
        if (Document is not { } doc || doc.Selection.Primary is not { } id || !_moveStarts.TryGetValue(id, out var start))
        {
            return;
        }

        var (dx, dy) = FrameDelta();
        double left = start.X, top = start.Y, right = start.Right, bottom = start.Bottom;
        if (_resizeEdges.HasFlag(ResizeEdges.Left))
        {
            left = Math.Min(start.X + dx, right - 1);
        }

        if (_resizeEdges.HasFlag(ResizeEdges.Right))
        {
            right = Math.Max(start.Right + dx, left + 1);
        }

        if (_resizeEdges.HasFlag(ResizeEdges.Top))
        {
            top = Math.Min(start.Y + dy, bottom - 1);
        }

        if (_resizeEdges.HasFlag(ResizeEdges.Bottom))
        {
            bottom = Math.Max(start.Bottom + dy, top + 1);
        }

        var proposed = new RectDto(left, top, right - left, bottom - top);
        var snapped = altDown ? new SnapResult(proposed, []) : SnapAgainstSiblings(doc, id, proposed, _resizeEdges);
        _proposed = snapped.Bounds;
        _snapLines = snapped.Lines;
    }

    private SnapResult SnapAgainstSiblings(ViewModels.DocumentViewModel doc, int id, RectDto proposed, ResizeEdges edges)
    {
        var parent = doc.Xaml.FindById(id)?.Parent;
        if (parent is null || doc.Layout.Find(parent.Id) is not { } container)
        {
            return new SnapResult(proposed, []);
        }

        var moving = _moveStarts.Keys.ToHashSet();
        var siblings = parent.Children
            .Where(c => !moving.Contains(c.Id))
            .Select(c => doc.Layout.Find(c.Id))
            .Where(l => l is { IsVisible: true })
            .Select(l => l!.Bounds)
            .ToList();
        var p = container.Padding;
        var content = new RectDto(container.Bounds.X + p.Left, container.Bounds.Y + p.Top,
            Math.Max(0, container.Bounds.Width - p.Left - p.Right), Math.Max(0, container.Bounds.Height - p.Top - p.Bottom));

        // Settings (§7.3): the grid first, then the guides, which win when another control's edge is close by.
        if (Services.DesignerOptions.SnapToGrid)
        {
            proposed = SnapEngine.SnapToGrid(proposed, edges, content, Services.DesignerOptions.GridSize);
        }

        return Services.DesignerOptions.SnapToControls
            ? SnapEngine.Snap(proposed, edges, siblings, content, SnapEngine.ThresholdScreen / DisplayFactor)
            : new SnapResult(proposed, []);
    }

    // ---- Rubber band ----

    private RectDto BandRect()
    {
        var factor = DisplayFactor;
        var x1 = Math.Min(_pressAt.X, _pointerAt.X) / factor;
        var y1 = Math.Min(_pressAt.Y, _pointerAt.Y) / factor;
        return new RectDto(x1, y1, Math.Abs(_pointerAt.X - _pressAt.X) / factor, Math.Abs(_pointerAt.Y - _pressAt.Y) / factor);
    }

    /// <summary>
    /// Selects the container's children that the band touches (§8.3). Touching rather than fully inside, because
    /// stretched children fill their panel exactly and a band drawn inside the panel could never contain them.
    /// </summary>
    private void UpdateBand()
    {
        _bandHits = [];
        if (Document is not { } doc || _containerForBand is not { } containerId || doc.Xaml.FindById(containerId) is not { } container)
        {
            return;
        }

        var band = BandRect();
        foreach (var child in container.Children)
        {
            if (doc.Layout.Find(child.Id) is { IsVisible: true } l
                && l.Bounds.X < band.Right && l.Bounds.Right > band.X && l.Bounds.Y < band.Bottom && l.Bounds.Bottom > band.Y)
            {
                _bandHits.Add(child.Id);
            }
        }
    }

    // ---- Handles ----

    private static readonly (double Fx, double Fy, ResizeEdges Edges)[] Handles =
    [
        (0, 0, ResizeEdges.Left | ResizeEdges.Top), (0.5, 0, ResizeEdges.Top), (1, 0, ResizeEdges.Right | ResizeEdges.Top),
        (0, 0.5, ResizeEdges.Left), (1, 0.5, ResizeEdges.Right),
        (0, 1, ResizeEdges.Left | ResizeEdges.Bottom), (0.5, 1, ResizeEdges.Bottom), (1, 1, ResizeEdges.Right | ResizeEdges.Bottom),
    ];

    /// <summary>Which edges the handle under <paramref name="position"/> resizes (primary selection only; never the root).</summary>
    private ResizeEdges? HandleAt(Point position)
    {
        if (Document is not { } doc || doc.Selection.Primary is not { } id || doc.Layout.Find(id) is not { } element
            || doc.Xaml.FindById(id)?.Parent is null)
        {
            return null;
        }

        var factor = DisplayFactor;
        var b = element.Bounds;
        foreach (var (fx, fy, edges) in Handles)
        {
            var hx = (b.X + b.Width * fx) * factor;
            var hy = (b.Y + b.Height * fy) * factor;
            if (Math.Abs(position.X - hx) <= HandleSize && Math.Abs(position.Y - hy) <= HandleSize)
            {
                return edges;
            }
        }

        return null;
    }

    private static InputSystemCursorShape CursorFor(ResizeEdges edges) => edges switch
    {
        ResizeEdges.Left | ResizeEdges.Top or ResizeEdges.Right | ResizeEdges.Bottom => InputSystemCursorShape.SizeNorthwestSoutheast,
        ResizeEdges.Right | ResizeEdges.Top or ResizeEdges.Left | ResizeEdges.Bottom => InputSystemCursorShape.SizeNortheastSouthwest,
        ResizeEdges.Top or ResizeEdges.Bottom => InputSystemCursorShape.SizeNorthSouth,
        _ => InputSystemCursorShape.SizeWestEast,
    };

    // ---- Drawing ----

    /// <summary>Drag feedback: moved-element ghosts, the resize outline with its size, the selection box, snaplines.</summary>
    private void DrawDragFeedback(double factor)
    {
        if (Document is not { } doc)
        {
            return;
        }

        if (_drag == DragKind.Move && _proposed is { } moved && doc.Selection.Primary is { } primary && _moveStarts.TryGetValue(primary, out var start))
        {
            var dx = moved.X - start.X;
            var dy = moved.Y - start.Y;
            foreach (var (_, bounds) in _moveStarts)
            {
                AdornerLayer.Children.Add(Ghost(bounds, dx, dy, factor));
                AdornerLayer.Children.Add(Outline(bounds with { X = bounds.X + dx, Y = bounds.Y + dy }, factor, thickness: 1, dashed: true));
            }
        }
        else if (_drag == DragKind.Resize && _proposed is { } resized)
        {
            AdornerLayer.Children.Add(Outline(resized, factor, thickness: 2, dashed: false));
            AddTag($"{Math.Round(resized.Width)} × {Math.Round(resized.Height)}", resized.X * factor, resized.Bottom * factor + 4);
        }
        else if (_drag == DragKind.Band)
        {
            var band = BandRect();
            AdornerLayer.Children.Add(Fill(band, factor, AccentBrush, 0.12));
            AdornerLayer.Children.Add(Outline(band, factor, thickness: 1, dashed: true));
            foreach (var id in _bandHits)
            {
                if (doc.Layout.Find(id) is { } l)
                {
                    AdornerLayer.Children.Add(Outline(l.Bounds, factor, thickness: 1, dashed: false));
                }
            }
        }

        foreach (var line in _snapLines)
        {
            AdornerLayer.Children.Add(new Line
            {
                X1 = line.X1 * factor,
                Y1 = line.Y1 * factor,
                X2 = line.X2 * factor,
                Y2 = line.Y2 * factor,
                Stroke = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                StrokeThickness = 1,
                StrokeDashArray = line.IsSpacing ? [4, 2] : null,
                IsHitTestVisible = false,
            });
        }
    }

    /// <summary>The element's pixels from the current frame, shifted by the drag delta.</summary>
    private Image Ghost(RectDto bounds, double dx, double dy, double factor)
    {
        var image = new Image
        {
            Source = FrameImage.Source,
            Width = FrameImage.Width,
            Height = FrameImage.Height,
            Stretch = Stretch.Fill,
            Opacity = 0.7,
            IsHitTestVisible = false,
            Clip = new RectangleGeometry { Rect = new Rect(bounds.X * factor, bounds.Y * factor, bounds.Width * factor, bounds.Height * factor) },
        };
        Canvas.SetLeft(image, dx * factor);
        Canvas.SetTop(image, dy * factor);
        return image;
    }

    private void AddTag(string text, double x, double y)
    {
        var tag = new Border
        {
            Background = AccentBrush,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 1),
            IsHitTestVisible = false,
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"] },
        };
        Canvas.SetLeft(tag, x);
        Canvas.SetTop(tag, y);
        AdornerLayer.Children.Add(tag);
    }
}
