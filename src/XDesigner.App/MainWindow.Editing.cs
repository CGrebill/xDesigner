using XDesigner.App.ViewModels;
using XDesigner.Core.Toolbox;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace XDesigner.App;

/// <summary>Toolbox, undo/redo, delete, save and the "save changes?" prompts (§6, §9.1, D11).</summary>
public sealed partial class MainWindow
{
    private bool _closeConfirmed;

    private void InitializeEditing()
    {
        Toolbox.ItemActivated += Toolbox_ItemActivated;
        AppWindow.Closing += AppWindow_Closing;

        // Editing shortcuts bubble up from wherever focus is. Text boxes handle their own Ctrl+C/V/X/Z/Y first
        // (marking the key handled), so these only act on the design when focus isn't in a text field.
        Root.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(Root_KeyDown), handledEventsToo: false);
        UpdateToolbox();
        UpdateEditState();
    }

    private void Toolbox_ItemActivated(ToolboxItem item)
    {
        if (ViewModel.SelectedDocument?.AddControlAtSelection(item) is { } problem)
        {
            ViewModel.AppendLog(problem);
        }
    }

    /// <summary>The toolbox shows the controls the selected document's renderer knows about.</summary>
    private void UpdateToolbox()
    {
        var doc = ViewModel.SelectedDocument;
        if (doc is null)
        {
            Toolbox.SetCategories([], "Open a file to see the controls you can add.");
        }
        else if (doc.Catalog.IsEmpty)
        {
            Toolbox.SetCategories([], "Loading controls…");
        }
        else
        {
            Toolbox.SetCategories(ToolboxCatalog.Build(doc.Catalog, doc.UserAssemblyName), "No controls available.");
        }
    }

    private void UpdateEditState()
    {
        var undo = ViewModel.SelectedDocument?.Undo;
        UndoMenuItem.IsEnabled = undo?.CanUndo == true;
        RedoMenuItem.IsEnabled = undo?.CanRedo == true;
        UndoMenuItem.Text = undo?.UndoDescription is { } u ? $"Undo {u}" : "Undo";
        RedoMenuItem.Text = undo?.RedoDescription is { } r ? $"Redo {r}" : "Redo";
    }

    private void Root_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl || e.OriginalSource is TextBox or PasswordBox or RichEditBox)
        {
            return;
        }

        Action? action = e.Key switch
        {
            Windows.System.VirtualKey.Z => () => ViewModel.SelectedDocument?.Undo.Undo(),
            Windows.System.VirtualKey.Y => () => ViewModel.SelectedDocument?.Undo.Redo(),
            Windows.System.VirtualKey.C => () => CopySelection(),
            Windows.System.VirtualKey.X => () => Cut_Click(this, new RoutedEventArgs()),
            Windows.System.VirtualKey.V => () => Paste_Click(this, new RoutedEventArgs()),
            _ => null,
        };

        if (action is not null)
        {
            action();
            e.Handled = true;
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => ViewModel.SelectedDocument?.Undo.Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => ViewModel.SelectedDocument?.Undo.Redo();

    private void Delete_Click(object sender, RoutedEventArgs e) => ViewModel.SelectedDocument?.DeleteSelection();

    private void Copy_Click(object sender, RoutedEventArgs e) => CopySelection();

    private void Cut_Click(object sender, RoutedEventArgs e)
    {
        if (CopySelection())
        {
            ViewModel.SelectedDocument?.DeleteSelection();
        }
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedDocument is not { } doc)
        {
            return;
        }

        string? text;
        try
        {
            text = await Services.ClipboardService.GetTextAsync();
        }
        catch (Exception ex)
        {
            ViewModel.AppendLog($"Couldn't read the clipboard: {ex.Message}");
            return;
        }

        if (text is not null && doc.Paste(text) is { } problem)
        {
            ViewModel.AppendLog(problem);
        }
    }

    private bool CopySelection()
    {
        if (ViewModel.SelectedDocument?.CopySelection() is not { } fragment)
        {
            return false;
        }

        try
        {
            Services.ClipboardService.SetFragment(fragment);
            return true;
        }
        catch (Exception ex)
        {
            ViewModel.AppendLog($"Couldn't copy to the clipboard: {ex.Message}");
            return false;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedDocument is { } doc)
        {
            TrySave(doc);
        }
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedDocument is { } doc && await ConfirmCloseAsync(doc))
        {
            await ViewModel.CloseDocumentCommand.ExecuteAsync(doc);
        }
    }

    private async void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is DocumentViewModel document && await ConfirmCloseAsync(document))
        {
            await ViewModel.CloseDocumentCommand.ExecuteAsync(document);
        }
    }

    private async void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmCloseAllAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    /// <summary>Closing the window with unsaved documents asks first (D11).</summary>
    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        Properties.FlushPendingEdits(); // a value still being typed counts as a change
        if (_closeConfirmed || !ViewModel.Documents.Any(d => d.IsDirty))
        {
            return;
        }

        args.Cancel = true;
        if (await ConfirmCloseAllAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private async Task<bool> ConfirmCloseAllAsync()
    {
        Properties.FlushPendingEdits();
        foreach (var doc in ViewModel.Documents.Where(d => d.IsDirty).ToList())
        {
            ViewModel.SelectedDocument = doc;
            if (!await ConfirmCloseAsync(doc))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Save / Don't save / Cancel for a document with unsaved changes. True when it may close.</summary>
    private async Task<bool> ConfirmCloseAsync(DocumentViewModel doc)
    {
        Properties.FlushPendingEdits(); // a value still being typed counts as a change
        if (!doc.IsDirty)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Save changes?",
            Content = $"{doc.Title} has unsaved changes.",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => TrySave(doc),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }

    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        Properties.FlushPendingEdits();
        foreach (var doc in ViewModel.Documents.Where(d => d.IsDirty).ToList())
        {
            TrySave(doc);
        }
    }

    private bool TrySave(DocumentViewModel doc)
    {
        Properties.FlushPendingEdits(); // Ctrl+S while typing in a property saves that value too
        try
        {
            doc.Save();
            ViewModel.AppendLog($"Saved {doc.FilePath}.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ViewModel.AppendLog($"Could not save {doc.Title}: {ex.Message}");
            return false;
        }
    }
}
