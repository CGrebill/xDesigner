using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Window = Microsoft.UI.Xaml.Window; // also compiled into the MAUI hosts, which import Microsoft.Maui.Controls globally

namespace XDesigner.RenderHost.Hosting;

/// <summary>
/// Where documents are drawn (§14.5): a canvas in a cloaked tool window (never seen, no taskbar button, never activated),
/// and the UI thread's work queue, one piece of work at a time. Shared by the WinUI and MAUI hosts (linked source: the
/// MAUI hosts pin their own Windows App SDK).
/// </summary>
internal sealed partial class RenderSurface : IDisposable
{
    private const int DwmwaCloak = 13;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatcherQueue _dispatcher;
    private readonly Window _window;

    private RenderSurface(DispatcherQueue dispatcher, Window window, Canvas canvas)
    {
        _dispatcher = dispatcher;
        _window = window;
        Canvas = canvas;
    }

    /// <summary>The canvas rendered documents are placed on (it may be smaller than they are).</summary>
    public Canvas Canvas { get; }

    /// <summary>Physical pixels per DIP of the window's monitor.</summary>
    public double RasterizationScale => Canvas.XamlRoot?.RasterizationScale is > 0 and var scale ? scale : 1.0;

    /// <summary>Creates the window on the current (UI) thread and waits until its canvas has loaded.</summary>
    public static async Task<RenderSurface> CreateAsync()
    {
        var canvas = new Canvas();
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        canvas.Loaded += (_, _) => loaded.TrySetResult();

        var window = new Window { Title = "xDesigner renderer", Content = canvas };
        Hide(WinRT.Interop.WindowNative.GetWindowHandle(window));
        window.AppWindow.IsShownInSwitchers = false;
        window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(800, 600));
        window.AppWindow.Show(activateWindow: false);

        try
        {
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("The renderer's window did not load.");
        }

        return new RenderSurface(DispatcherQueue.GetForCurrentThread(), window, canvas);
    }

    /// <summary>Makes a window invisible for good: cloaked, a tool window (no taskbar button) that is never activated.</summary>
    public static void Hide(nint hwnd)
    {
        var cloak = 1;
        _ = DwmSetWindowAttribute(hwnd, DwmwaCloak, ref cloak, sizeof(int));
        _ = SetWindowLongPtr(hwnd, GwlExStyle, GetWindowLongPtr(hwnd, GwlExStyle) | WsExToolWindow | WsExNoActivate);
    }

    /// <summary>Runs <paramref name="work"/> on the UI thread, one piece of work at a time (a render awaits the GPU in between).</summary>
    public Task<T> InvokeAsync<T>(Func<T> work) => InvokeTaskAsync(() => Task.FromResult(work()));

    /// <inheritdoc cref="InvokeAsync{T}(Func{T})"/>
    public Task<T> InvokeTaskAsync<T>(Func<Task<T>> work)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _dispatcher.TryEnqueue(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                result.TrySetResult(await work());
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
            finally
            {
                _gate.Release();
            }
        });
        if (!queued)
        {
            result.TrySetException(new InvalidOperationException("The renderer's UI thread has stopped."));
        }

        return result.Task;
    }

    /// <summary>Closes the window (the host is exiting).</summary>
    public void Dispose()
    {
        try
        {
            _window.Close();
        }
        catch
        {
            // Exiting anyway.
        }

        _gate.Dispose();
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
