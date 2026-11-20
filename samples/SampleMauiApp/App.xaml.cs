namespace SampleMauiApp;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(new MainPage()) { Title = "Sample MAUI App" };
}
