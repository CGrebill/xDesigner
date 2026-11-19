using XDesigner.Core.Editing;
using Windows.ApplicationModel.DataTransfer;

namespace XDesigner.App.Services;

/// <summary>
/// Windows clipboard access for designer fragments (§13): a private format plus plain text, so a copied control
/// can also be pasted into a text editor as XAML (and plain XAML text can be pasted back in).
/// </summary>
public static class ClipboardService
{
    public static void SetFragment(string fragment)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetData(DesignerActions.ClipboardFormat, fragment);
        package.SetText(fragment);
        Clipboard.SetContent(package);
        Clipboard.Flush(); // keep it available after the designer closes
    }

    /// <summary>The designer fragment if there is one, else any text; null when the clipboard has neither.</summary>
    public static async Task<string?> GetTextAsync()
    {
        var content = Clipboard.GetContent();
        if (content.Contains(DesignerActions.ClipboardFormat) && await content.GetDataAsync(DesignerActions.ClipboardFormat) is string fragment)
        {
            return fragment;
        }

        return content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : null;
    }
}
