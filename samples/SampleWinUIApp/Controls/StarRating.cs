using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SampleWinUIApp.Controls;

/// <summary>
/// A control written in code (no XAML file), used to check that the designer renders and inspects controls from the
/// user's own assembly, including their dependency properties.
/// </summary>
public sealed partial class StarRating : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(int), typeof(StarRating), new PropertyMetadata(0, OnChanged));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(int), typeof(StarRating), new PropertyMetadata(5, OnChanged));

    private readonly StackPanel _stars = new() { Orientation = Orientation.Horizontal, Spacing = 2 };

    public StarRating()
    {
        Content = _stars;
        Rebuild();
    }

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((StarRating)d).Rebuild();

    private void Rebuild()
    {
        _stars.Children.Clear();
        for (var i = 0; i < Maximum; i++)
        {
            _stars.Children.Add(new TextBlock
            {
                Text = i < Value ? "★" : "☆",
                FontSize = 22,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Goldenrod),
            });
        }
    }
}
