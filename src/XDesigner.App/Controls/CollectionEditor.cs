using XDesigner.App.Services;
using XDesigner.Core.Editing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace XDesigner.App.Controls;

/// <summary>
/// The Collection Editor (§9.4): the items of a ListBox, ComboBox, TabControl, Menu or TreeView in a list (sub-items
/// indented) with Add / Add sub-item / Remove / Up / Down, and their text on the right. Works on a copy; OK applies it
/// as one undo step, Cancel leaves the document alone.
/// </summary>
public static class CollectionEditor
{
    /// <summary>One visible line: an item, where it lives, and how deep it is.</summary>
    private sealed class Row(ItemDraft draft, List<ItemDraft> owner, int depth)
    {
        public ItemDraft Draft { get; } = draft;

        public List<ItemDraft> Owner { get; } = owner;

        public int Depth { get; } = depth;

        public override string ToString() => new string(' ', Depth * 4) + (Draft.Text.Length > 0 ? Draft.Text : "(no text)");
    }

    /// <summary>Shows the editor; returns the edited items, or null when cancelled.</summary>
    public static async Task<List<ItemDraft>?> ShowAsync(XamlRoot root, string controlName, ItemsKind kind, IReadOnlyList<ItemDraft> items)
    {
        var drafts = items.Select(Clone).ToList();
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, Height = 300, Width = 260, BorderThickness = new Thickness(1) };
        list.BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
        var text = new TextBox { Header = kind.TextProperty == "Header" ? "Header" : "Text", Width = 260 };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Width = 260, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        var add = new Button { Content = $"Add {kind.Noun}" };
        var addChild = new Button { Content = "Add sub-item", Visibility = kind.Nested ? Visibility.Visible : Visibility.Collapsed };
        var remove = new Button { Content = "Remove" };
        var up = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 } };
        var down = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 } };
        ToolTipService.SetToolTip(up, "Move up");
        ToolTipService.SetToolTip(down, "Move down");
        var updating = false;
        var counter = drafts.Count + 1;

        List<Row> Flatten()
        {
            var rows = new List<Row>();
            void Walk(List<ItemDraft> level, int depth)
            {
                foreach (var d in level)
                {
                    rows.Add(new Row(d, level, depth));
                    Walk(d.Children, depth + 1);
                }
            }

            Walk(drafts, 0);
            return rows;
        }

        void Refresh(ItemDraft? select)
        {
            updating = true;
            var rows = Flatten();
            list.ItemsSource = rows;
            list.SelectedItem = rows.FirstOrDefault(r => r.Draft == select) ?? rows.FirstOrDefault();
            updating = false;
            ShowSelected();
        }

        Row? Selected() => list.SelectedItem as Row;

        void ShowSelected()
        {
            var row = Selected();
            updating = true;
            text.Text = row?.Draft.Text ?? "";
            updating = false;
            text.IsEnabled = row?.Draft.TextEditable == true;
            note.Text = row is null ? $"No {kind.Noun}s yet. Press Add {kind.Noun}."
                : !row.Draft.TextEditable ? "This item's content is a control or other XAML: its text can't be edited here."
                : row.Draft.ExistingId is null ? $"New {kind.Noun}{(kind.ItemType == "TabItem" ? " (with an empty Grid to drop controls into)" : "")}."
                : "";
            remove.IsEnabled = up.IsEnabled = down.IsEnabled = addChild.IsEnabled = row is not null;
        }

        list.SelectionChanged += (_, _) =>
        {
            if (!updating)
            {
                ShowSelected();
            }
        };
        text.TextChanged += (_, _) =>
        {
            // TextChanged arrives after a programmatic set too (asynchronously): only a different text is the user's edit.
            if (!updating && Selected() is { } row && row.Draft.TextEditable && row.Draft.Text != text.Text)
            {
                row.Draft.Text = text.Text;
                var caret = text.SelectionStart;
                Refresh(row.Draft);
                text.SelectionStart = caret;
                text.Focus(FocusState.Programmatic);
            }
        };
        add.Click += (_, _) =>
        {
            var draft = new ItemDraft(null, $"{Capitalized(kind.Noun)} {counter++}");
            if (Selected() is { } row)
            {
                row.Owner.Insert(row.Owner.IndexOf(row.Draft) + 1, draft);
            }
            else
            {
                drafts.Add(draft);
            }

            Refresh(draft);
        };
        addChild.Click += (_, _) =>
        {
            if (Selected() is { } row)
            {
                var draft = new ItemDraft(null, $"{Capitalized(kind.Noun)} {counter++}");
                row.Draft.Children.Add(draft);
                Refresh(draft);
            }
        };
        remove.Click += (_, _) =>
        {
            if (Selected() is { } row)
            {
                var index = row.Owner.IndexOf(row.Draft);
                row.Owner.RemoveAt(index);
                Refresh(row.Owner.ElementAtOrDefault(Math.Min(index, row.Owner.Count - 1)));
            }
        };
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(+1);

        void Move(int by)
        {
            if (Selected() is { } row)
            {
                var index = row.Owner.IndexOf(row.Draft);
                var target = index + by;
                if (target >= 0 && target < row.Owner.Count)
                {
                    row.Owner.RemoveAt(index);
                    row.Owner.Insert(target, row.Draft);
                    Refresh(row.Draft);
                }
            }
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { add, addChild, remove, up, down } };
        var layout = new Grid { ColumnSpacing = 16, RowSpacing = 8 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var right = new StackPanel { Spacing = 8, Children = { text, note } };
        Grid.SetColumn(right, 1);
        Grid.SetRow(buttons, 1);
        Grid.SetColumnSpan(buttons, 2);
        layout.Children.Add(list);
        layout.Children.Add(right);
        layout.Children.Add(buttons);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"Edit {kind.Noun}s of {controlName}",
            Content = layout,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 720d; // the default 548 clips the right-hand column
        Refresh(drafts.FirstOrDefault());
        return await dialog.ShowOnceAsync() == ContentDialogResult.Primary ? drafts : null;
    }

    private static ItemDraft Clone(ItemDraft d)
    {
        var copy = new ItemDraft(d.ExistingId, d.Text, d.TextEditable);
        copy.Children.AddRange(d.Children.Select(Clone));
        return copy;
    }

    private static string Capitalized(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
