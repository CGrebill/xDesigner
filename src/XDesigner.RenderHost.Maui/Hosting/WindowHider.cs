using System.Runtime.InteropServices;

namespace XDesigner.RenderHost.Hosting;

/// <summary>
/// Keeps windows opened on this thread invisible and inactive while installed (§14.6): MAUI's startup opens the project's
/// main window, which the designer needs only for its MAUI context, and the project may open more later (from a timer, or
/// <c>Application.OpenWindow</c>). A thread-local CBT hook, installed for the host's lifetime, cloaks each top-level window
/// as it is created (one shown without activation stays hidden too) and refuses every activation, so nothing appears and
/// focus stays where it is (review M29). The renderer draws off-screen, so no window of its own needs to be seen either.
/// </summary>
internal static partial class WindowHider
{
    private const int WhCbt = 5;
    private const int HcbtCreateWnd = 3;
    private const int HcbtActivate = 5;
    private const int WsChild = 0x40000000;

    private static HookProc? s_proc; // kept alive while the hook is installed

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    /// <summary>Installs the hook on this (the UI) thread; throws when Windows refuses, rather than letting windows appear (review L46).</summary>
    public static IDisposable Install()
    {
        s_proc = OnHook;
        var hook = SetWindowsHookExW(WhCbt, Marshal.GetFunctionPointerForDelegate(s_proc), 0, GetCurrentThreadId());
        if (hook == 0)
        {
            throw new InvalidOperationException($"The window hook couldn't be installed (error {Marshal.GetLastPInvokeError()}), so the project's windows would show.");
        }

        return new Hook(hook);
    }

    private static nint OnHook(int code, nint wParam, nint lParam)
    {
        if (code == HcbtActivate)
        {
            RenderSurface.Hide(wParam);
            return 1; // don't activate
        }

        if (code == HcbtCreateWnd && IsTopLevel(lParam))
        {
            RenderSurface.Hide(wParam);
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    /// <summary>
    /// From the CBT_CREATEWND being created: no parent and no WS_CHILD style. Its CREATESTRUCT is lpCreateParams, hInstance,
    /// hMenu, hwndParent (pointers), then cy, cx, y, x and style (ints).
    /// </summary>
    private static bool IsTopLevel(nint createWnd)
    {
        var create = Marshal.ReadIntPtr(createWnd);
        if (create == 0)
        {
            return false;
        }

        var parent = Marshal.ReadIntPtr(create, 3 * IntPtr.Size);
        var style = Marshal.ReadInt32(create, (4 * IntPtr.Size) + (4 * sizeof(int)));
        return parent == 0 && (style & WsChild) == 0;
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
