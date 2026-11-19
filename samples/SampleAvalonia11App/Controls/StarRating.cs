using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SampleAvaloniaApp.Controls;

/// <summary>
/// A custom control that draws itself (no template), used to check that the designer
/// renders and inspects controls from the user's own assembly.
/// </summary>
public class StarRating : Control
{
    public static readonly StyledProperty<int> ValueProperty =
        AvaloniaProperty.Register<StarRating, int>(nameof(Value), 0);

    public static readonly StyledProperty<int> MaximumProperty =
        AvaloniaProperty.Register<StarRating, int>(nameof(Maximum), 5);

    public static readonly StyledProperty<IBrush?> StarBrushProperty =
        AvaloniaProperty.Register<StarRating, IBrush?>(nameof(StarBrush), Brushes.Goldenrod);

    public static readonly StyledProperty<double> StarSizeProperty =
        AvaloniaProperty.Register<StarRating, double>(nameof(StarSize), 24);

    static StarRating()
    {
        AffectsRender<StarRating>(ValueProperty, MaximumProperty, StarBrushProperty);
        AffectsMeasure<StarRating>(MaximumProperty, StarSizeProperty);
    }

    public int Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public IBrush? StarBrush
    {
        get => GetValue(StarBrushProperty);
        set => SetValue(StarBrushProperty, value);
    }

    public double StarSize
    {
        get => GetValue(StarSizeProperty);
        set => SetValue(StarSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Max(0, Maximum) * StarSize, StarSize);

    public override void Render(DrawingContext context)
    {
        var outline = new Pen(Brushes.Gray, 1);
        for (var i = 0; i < Maximum; i++)
        {
            var geometry = CreateStar(new Point(i * StarSize + StarSize / 2, StarSize / 2), StarSize / 2 - 1);
            context.DrawGeometry(i < Value ? StarBrush : null, outline, geometry);
        }
    }

    private static StreamGeometry CreateStar(Point center, double outerRadius)
    {
        var innerRadius = outerRadius * 0.45;
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        for (var i = 0; i < 10; i++)
        {
            var radius = i % 2 == 0 ? outerRadius : innerRadius;
            var angle = Math.PI / 5 * i - Math.PI / 2;
            var point = new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
            if (i == 0)
            {
                ctx.BeginFigure(point, isFilled: true);
            }
            else
            {
                ctx.LineTo(point);
            }
        }

        ctx.EndFigure(isClosed: true);
        return geometry;
    }
}
