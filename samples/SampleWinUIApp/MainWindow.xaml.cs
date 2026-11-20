using Microsoft.UI.Xaml;

namespace SampleWinUIApp;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void button1_Click(object sender, RoutedEventArgs e)
    {
        titleText.Text = "Clicked";
    }
}
