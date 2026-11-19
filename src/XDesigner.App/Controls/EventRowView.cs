using XDesigner.Core.Properties;
using XDesigner.Protocol.Metadata;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace XDesigner.App.Controls;

/// <summary>What event rows need from the properties window.</summary>
internal sealed class EventEditorContext
{
    /// <summary>Connects the event on the elements to a method (null = the default <c>{control}_{Event}</c> name).</summary>
    public required Action<IReadOnlyList<int>, EventInfoDto, string?> Connect { get; init; }

    /// <summary>Removes the event attribute from the elements.</summary>
    public required Action<IReadOnlyList<int>, string> Disconnect { get; init; }

    /// <summary>Existing code-behind methods that fit the event.</summary>
    public required Func<EventInfoDto, IReadOnlyList<(string Name, int Line)>> CompatibleMethods { get; init; }
}

/// <summary>
/// One row of the Events tab (§9.3): event name, the handler's method name, a list of fitting existing methods, and a
/// create/show button. Double-clicking the row creates the default handler (or shows the existing one). Typing a name and
/// pressing Enter connects it (the method is created if it doesn't exist); clearing the name disconnects (the method stays).
/// </summary>
internal sealed class EventRowView
{
    private readonly EventEditorContext _context;
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBox _box = new() { MinWidth = 0, PlaceholderText = "(none)" };
    private readonly Button _go;
    private readonly FontIcon _goIcon = new() { FontSize = 12 };
    private IReadOnlyList<int> _targets = [];
    private string _applied = "";

    public EventRowView(EventRow row, EventEditorContext context)
    {
        _context = context;
        Row = row;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_box, row.Name);

        var list = new ListView { Width = 260, MaxHeight = 320, SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
        var flyout = new Flyout { Content = list };
        flyout.Opening += (_, _) =>
        {
            var methods = _context.CompatibleMethods(Row.Info).Select(m => m.Name).ToList();
            list.ItemsSource = methods.Count > 0 ? methods : new List<string> { "(no fitting methods in the code-behind yet)" };
        };
        list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is string name && !name.StartsWith('('))
            {
                flyout.Hide();
                _box.Text = name;
                Commit(name);
            }
        };
        var pick = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(6, 4, 6, 4), MinWidth = 0, Flyout = flyout };
        ToolTipService.SetToolTip(pick, "Choose an existing method");

        _go = new Button { Content = _goIcon, Padding = new Thickness(6, 4, 6, 4), MinWidth = 0 };
        _go.Click += (_, _) => CreateOrShow();

        _box.PreviewKeyDown += Box_KeyDown;
        _box.LostFocus += (_, _) => Commit(_box.Text);
        _box.DoubleTapped += Row_DoubleTapped;
        _label.DoubleTapped += Row_DoubleTapped;

        View = new Grid { ColumnSpacing = 4, Padding = new Thickness(12, 2, 6, 2), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        View.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.42, GridUnitType.Star) });
        View.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.58, GridUnitType.Star) });
        View.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        View.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_box, 1);
        Grid.SetColumn(pick, 2);
        Grid.SetColumn(_go, 3);
        View.Children.Add(_label);
        View.Children.Add(_box);
        View.Children.Add(pick);
        View.Children.Add(_go);
    }

    public EventRow Row { get; private set; }

    public Grid View { get; }

    public bool HasPendingEdit => _box.Text != _applied;

    public void Update(EventRow row, IReadOnlyList<int> targets)
    {
        Row = row;
        _label.Text = row.Name;
        _label.FontWeight = row.IsSet ? FontWeights.SemiBold : FontWeights.Normal;
        ToolTipService.SetToolTip(_label, $"{row.Name}{(row.Info.IsRouted ? " (routed event)" : "")}\n" +
            $"Handler: void Name({row.Info.SenderType} sender, {row.Info.ArgsType} e)\nDouble-click to create the handler.");
        _goIcon.Glyph = row.Handler is null ? "" : "";
        ToolTipService.SetToolTip(_go, row.Handler is null ? "Create a handler for this event" : $"Show where {row.Handler} is");
        if (HasPendingEdit)
        {
            return; // keep what is being typed (and the elements it was typed for)
        }

        _targets = targets;
        _box.Text = row.IsMixed ? "" : row.Handler ?? "";
        _box.PlaceholderText = row.IsMixed ? "(different handlers)" : "(none)";
        _applied = _box.Text;
    }

    public void Flush() => Commit(_box.Text);

    private void Box_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            Commit(_box.Text);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape && HasPendingEdit)
        {
            _box.Text = _applied;
            e.Handled = true;
        }
    }

    private void Row_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        CreateOrShow();
    }

    /// <summary>No handler yet: create the default one. Already connected: make sure the method exists and say where it is.</summary>
    private void CreateOrShow()
    {
        if (HasPendingEdit)
        {
            Commit(_box.Text);
            return;
        }

        _context.Connect(_targets, Row.Info, Row.IsMixed ? null : Row.Handler);
    }

    private void Commit(string text)
    {
        if (text == _applied)
        {
            return;
        }

        var name = text.Trim();
        _applied = text;
        if (name.Length == 0)
        {
            _context.Disconnect(_targets, Row.Name);
        }
        else
        {
            _context.Connect(_targets, Row.Info, name);
        }
    }
}
