namespace $rootnamespace$.WinUI;

/// <summary>The app on Windows: MAUI's WinUI application, with the app's MauiProgram.</summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
