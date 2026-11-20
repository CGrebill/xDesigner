namespace SampleMauiApp.Controls;

/// <summary>
/// A control written in code (no XAML file), used to check that the designer renders and inspects controls from the user's
/// own assembly, including their bindable properties.
/// </summary>
public class StarRating : ContentView
{
    public static readonly BindableProperty ValueProperty =
        BindableProperty.Create(nameof(Value), typeof(int), typeof(StarRating), 0, propertyChanged: OnChanged);

    public static readonly BindableProperty MaximumProperty =
        BindableProperty.Create(nameof(Maximum), typeof(int), typeof(StarRating), 5, propertyChanged: OnChanged);

    private readonly HorizontalStackLayout _stars = new() { Spacing = 2 };

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

    private static void OnChanged(BindableObject bindable, object oldValue, object newValue) => ((StarRating)bindable).Rebuild();

    private void Rebuild()
    {
        _stars.Children.Clear();
        for (var i = 0; i < Maximum; i++)
        {
            _stars.Children.Add(new Label { Text = i < Value ? "★" : "☆", FontSize = 22, TextColor = Colors.Goldenrod });
        }
    }
}
