using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace $rootnamespace$;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            // async/await continues on the UI thread, as in any WinUI app.
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
