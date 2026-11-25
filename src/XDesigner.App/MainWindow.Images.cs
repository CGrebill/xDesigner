using XDesigner.App.ViewModels;
using XDesigner.Core.Assets;
using XDesigner.Core.Project;
using Windows.Storage.Pickers;

namespace XDesigner.App;

/// <summary>
/// The image picker's Browse… (§12): copy the picked file into the project's images folder (Assets; MAUI ResourcesImages) (an identical file already
/// there is reused), make sure the project packages Assets, set the property, then rebuild so the renderer has it.
/// </summary>
public sealed partial class MainWindow
{
    private void InitializeImages() => Properties.BrowseImage = BrowseImageAsync;

    private async Task BrowseImageAsync(DocumentViewModel document, Action<string> commit)
    {
        if (document.ProjectDirectory is not { } directory || document.Project is not { } project)
        {
            ShowNotification(NotifyKind.Warning, "Images are packaged with a project. Open the project this file belongs to first.");
            return;
        }

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
        foreach (var extension in ProjectAssets.ImageExtensionsFor(document.Xaml.Profile))
        {
            picker.FileTypeFilter.Add(extension);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (await picker.PickSingleFileAsync() is not { } file)
        {
            return;
        }

        AssetImport import;
        bool projectChanged;
        try
        {
            import = await Task.Run(() => ProjectAssets.Import(directory, file.Path, document.Xaml.Profile));
            projectChanged = await Task.Run(() => ProjectFileEditor.EnsureAssetsItem(project.ProjectPath, document.Xaml.Profile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException)
        {
            ShowNotification(NotifyKind.Error, $"Couldn't add {file.Name} to the project: {ex.Message}");
            return;
        }

        if (import.Copied)
        {
            ViewModel.AppendLog($"Copied {file.Path} to {import.FilePath}.");
        }

        if (projectChanged)
        {
            ViewModel.AppendLog($"Added {ProjectFileEditor.AssetsItemFor(document.Xaml.Profile)} to {Path.GetFileName(project.ProjectPath)} so the image is packaged with the app.");
        }

        commit(import.XamlPath);

        // The renderer reads images from the built assembly: a new file needs a build before it shows.
        if (import.Copied || projectChanged || document.UserAssemblyPath is null)
        {
            // Any project of the open solution: a build builds them all, not only the startup project (review M31).
            if (ViewModel.ProjectOf(document.FilePath) is not null || (ViewModel.Project is { } open && open.Contains(document.FilePath)))
            {
                ViewModel.AppendLog($"Rebuilding so {import.XamlPath} shows in the designer…");
                await ViewModel.BuildAsync(saveDocuments: false);
            }
            else
            {
                ShowNotification(NotifyKind.Warning,
                    $"{import.XamlPath} was added to the project. Open the project (Project ▸ Open project) or build it to see the image here.");
            }
        }
    }
}
