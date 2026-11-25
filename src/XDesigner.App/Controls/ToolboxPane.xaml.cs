using XDesigner.App.Services;
using XDesigner.Core.Toolbox;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace XDesigner.App.Controls;

/// <summary>The toolbox (§9.1): categories of controls, a search box, drag sources.</summary>
public sealed partial class ToolboxPane : UserControl
{
    private IReadOnlyList<ToolboxCategory> _categories = [];

    public ToolboxPane()
    {
        InitializeComponent();
    }

    /// <summary>A toolbox item was double-clicked (add it to the selected container).</summary>
    public event Action<ToolboxItem>? ItemActivated;

    /// <summary>Shows <paramref name="categories"/>, or <paramref name="emptyMessage"/> when there are none.</summary>
    public void SetCategories(IReadOnlyList<ToolboxCategory> categories, string emptyMessage)
    {
        _categories = categories;
        EmptyText.Text = emptyMessage;
        Rebuild();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        CategoryHost.Children.Clear();
        var filter = SearchBox.Text.Trim();
        var searching = filter.Length > 0;

        foreach (var category in _categories)
        {
            var items = searching
                ? category.Items.Where(i => i.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList()
                : category.Items.ToList();
            if (items.Count == 0)
            {
                continue;
            }

            var list = new ListView
            {
                ItemsSource = items,
                ItemTemplate = (DataTemplate)Resources["ToolboxItemTemplate"],
                SelectionMode = ListViewSelectionMode.None,
                CanDragItems = true,
                IsItemClickEnabled = false,
            };
            list.DragItemsStarting += List_DragItemsStarting;
            list.DragItemsCompleted += (_, _) => ToolboxDrag.Current = null;
            list.DoubleTapped += List_DoubleTapped;

            CategoryHost.Children.Add(new Expander
            {
                Header = $"{category.Name} ({items.Count})",
                Content = list,
                IsExpanded = searching || !category.StartsCollapsed,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
            });
        }

        EmptyText.Visibility = CategoryHost.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (searching && CategoryHost.Children.Count == 0)
        {
            EmptyText.Text = $"No controls match “{filter}”.";
        }
    }

    private static void List_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is ToolboxItem item)
        {
            ToolboxDrag.Current = item;
            e.Data.SetText(ToolboxDrag.DataPrefix + item.Type.FullName); // for other apps (the designer itself reads ToolboxDrag.Current)
            e.Data.RequestedOperation = DataPackageOperation.Copy;
        }
        else
        {
            e.Cancel = true;
        }
    }

    private void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ToolboxItem item)
        {
            ItemActivated?.Invoke(item);
            e.Handled = true;
        }
    }
}
