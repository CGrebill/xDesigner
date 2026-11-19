using XDesigner.App.Controls;
using XDesigner.App.ViewModels;
using XDesigner.Core.Editing;
using Microsoft.UI.Xaml;

namespace XDesigner.App;

/// <summary>The context menu's host: clipboard and the Collection / Grid editors (§9.4, §9.5).</summary>
public sealed partial class MainWindow : IDesignCommandHost
{
    void IDesignCommandHost.Cut() => Cut_Click(this, new RoutedEventArgs());

    void IDesignCommandHost.Copy() => CopySelection();

    void IDesignCommandHost.Paste() => Paste_Click(this, new RoutedEventArgs());

    void IDesignCommandHost.ShowMessage(string message) => ShowNotification(NotifyKind.Warning, message);

    async Task IDesignCommandHost.EditItemsAsync(DocumentViewModel document, int id)
    {
        if (document.Xaml.FindById(id) is not { } node || ItemsEditing.KindOf(document.Catalog.Resolve(node)) is not { } kind)
        {
            return;
        }

        var edited = await CollectionEditor.ShowAsync(Content.XamlRoot, node.DisplayName, kind, ItemsEditing.Read(node, kind));
        if (edited is not null && document.Xaml.FindById(id) is not null)
        {
            document.Actions.SetItems(id, edited, kind);
        }
    }

    async Task IDesignCommandHost.EditGridAsync(DocumentViewModel document, int id)
    {
        if (document.Xaml.FindById(id) is not { } grid)
        {
            return;
        }

        var rows = GridDefinitions.Read(grid.Element, rows: true);
        var columns = GridDefinitions.Read(grid.Element, rows: false);
        if (rows is null || columns is null)
        {
            ShowNotification(NotifyKind.Warning, $"{grid.DisplayName}'s rows or columns are written in a form the designer can't edit (a binding, a resource, a comment, or settings such as SharedSizeGroup).");
            return;
        }

        if (await GridDefinitionsEditor.ShowAsync(Content.XamlRoot, grid.DisplayName, rows, columns) is { } result && document.Xaml.FindById(id) is not null)
        {
            document.Actions.SetGridDefinitions(id, result.Rows, result.Columns);
        }
    }
}
