using Microsoft.UI.Dispatching;

namespace SampleMauiApp.WinUI;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            // async/await continues on the UI thread, as in any Windows app.
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
