namespace SampleMauiApp;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    private void button1_Clicked(object? sender, EventArgs e)
    {
        titleLabel.Text = "Clicked";
    }
}
