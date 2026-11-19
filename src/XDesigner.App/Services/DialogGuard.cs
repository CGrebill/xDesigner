using Microsoft.UI.Xaml.Controls;

namespace XDesigner.App.Services;

/// <summary>
/// WinUI shows one <see cref="ContentDialog"/> at a time and throws if a second one is opened (clicking the window's
/// close button again while "Save changes?" is up, a shortcut while a dialog is open). Every dialog goes through
/// <see cref="ShowOnceAsync"/>, which answers a second request as if it had been cancelled.
/// </summary>
internal static class DialogGuard
{
    /// <summary>A dialog is open right now.</summary>
    public static bool IsOpen { get; private set; }

    /// <summary>Shows the dialog, or returns <see cref="ContentDialogResult.None"/> (Cancel) when another one is open.</summary>
    public static async Task<ContentDialogResult> ShowOnceAsync(this ContentDialog dialog)
    {
        if (IsOpen)
        {
            return ContentDialogResult.None;
        }

        IsOpen = true;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            IsOpen = false;
        }
    }
}
