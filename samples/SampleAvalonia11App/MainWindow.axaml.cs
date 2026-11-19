using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SampleAvaloniaApp;

public partial class MainWindow : Window
{
    private int _clicks;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void button1_Click(object? sender, RoutedEventArgs e)
    {
        _clicks++;
        titleText.Text = $"Clicked {_clicks} time(s)";
        starRating1.Value = _clicks % (starRating1.Maximum + 1);
    }
}
