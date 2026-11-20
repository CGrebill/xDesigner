using System.Runtime.InteropServices;

namespace XDesigner.RenderHost.Hosting;

/// <summary>
/// Keeps windows opened on this thread invisible and inactive while installed (§14.6): MAUI's startup opens the project's
/// main window, which the designer needs only for its MAUI context. A thread-local CBT hook hides each window as it is
/// activated and refuses the activation, so nothing appears and focus stays where it is.
/// </summary>
internal static partial class WindowHider
{
    private const int WhCbt = 5;
    private const int HcbtActivate = 5;

    private static HookProc? s_proc; // kept alive while the hook is installed

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    public static IDisposable Install()
    {
        s_proc = OnHook;
        var hook = SetWindowsHookExW(WhCbt, Marshal.GetFunctionPointerForDelegate(s_proc), 0, GetCurrentThreadId());
        return new Hook(hook);
    }

    private static nint OnHook(int code, nint wParam, nint lParam)
    {
        if (code == HcbtActivate)
        {
            RenderSurface.Hide(wParam);
            return 1; // don't activate
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    private sealed class Hook(nint handle) : IDisposable
    {
        public void Dispose()
        {
            if (handle != 0)
            {
                UnhookWindowsHookEx(handle);
            }
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowsHookExW(int idHook, nint proc, nint module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
