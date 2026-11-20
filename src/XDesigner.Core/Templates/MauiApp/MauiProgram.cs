namespace $rootnamespace$;

public static class MauiProgram
{
    // Also used by xDesigner to show your pages with your app's styles, fonts and handlers.
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        return builder.Build();
    }
}
