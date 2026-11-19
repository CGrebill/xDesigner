using Avalonia;

namespace $rootnamespace$;

internal static class Program
{
    // Don't use Avalonia (or anything that uses it) before Main has called StartWithClassicDesktopLifetime.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by xDesigner to show your windows with your app's theme and fonts.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
