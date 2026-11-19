using XDesigner.App.Services;
using XDesigner.Core.Layout;
using XDesigner.Protocol.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;

namespace XDesigner.App.Controls;

/// <summary>Dropping toolbox controls onto the form (§7.1, §8.3): live placement feedback, then one undoable add.</summary>
public sealed partial class DesignSurface
{
    private DropPlan? _dropPlan;

    private void AdornerLayer_DragOver(object sender, DragEventArgs e)
    {
        if (ToolboxDrag.Current is not { } item || Document is not { } doc)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var position = e.GetPosition(AdornerLayer);
        var factor = DisplayFactor;
        var plan = DropPlanner.Plan(doc.Xaml, doc.Catalog, doc.Layout, position.X / factor, position.Y / factor, out var reason);

        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = false;
        if (plan is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.Caption = doc.Catalog.IsEmpty ? "Still loading the controls…" : reason ?? "Can't drop here";
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            var target = doc.Xaml.FindById(plan.ContainerId)?.DisplayName ?? "form";
            e.DragUIOverride.Caption = $"Add {item.DisplayName} to {target}";
        }

        if (!Equals(plan, _dropPlan))
        {
            _dropPlan = plan;
            RedrawAdorners();
        }
    }

    private void AdornerLayer_DragLeave(object sender, DragEventArgs e) => ClearDrop();

    private void AdornerLayer_Drop(object sender, DragEventArgs e)
    {
        var plan = _dropPlan;
        var item = ToolboxDrag.Current;
        ClearDrop();
        if (plan is not null && item is not null && Document is { } doc)
        {
            doc.AddControl(item, plan);
            Focus(FocusState.Programmatic);
        }
    }

    private void ClearDrop()
    {
        if (_dropPlan is not null)
        {
            _dropPlan = null;
            RedrawAdorners();
        }
    }

    /// <summary>Drop feedback: the receiving container, plus the cell, band or insertion line.</summary>
    private void DrawDropIndicator(double factor)
    {
        if (_dropPlan?.Indicator is not { } indicator)
        {
            return;
        }

        var accent = AccentBrush;
        AdornerLayer.Children.Add(Outline(indicator.Container, factor, thickness: 2, dashed: true));

        if (indicator.Area is { } area)
        {
            AdornerLayer.Children.Add(Fill(area, factor, accent, 0.18));
        }

        if (indicator.Line is { } line)
        {
            AdornerLayer.Children.Add(Fill(line, factor, accent, 1));
        }

        if (indicator.Kind == DropIndicatorKind.Container)
        {
            AdornerLayer.Children.Add(Fill(indicator.Container, factor, accent, 0.12));
        }
    }

    private static Rectangle Fill(RectDto bounds, double factor, Brush brush, double opacity)
    {
        var rect = new Rectangle
        {
            Width = Math.Max(2, bounds.Width * factor),
            Height = Math.Max(2, bounds.Height * factor),
            Fill = brush,
            Opacity = opacity,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(rect, bounds.X * factor);
        Canvas.SetTop(rect, bounds.Y * factor);
        return rect;
    }
}
