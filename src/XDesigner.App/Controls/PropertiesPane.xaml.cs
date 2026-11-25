using System.ComponentModel;
using XDesigner.App.ViewModels;
using XDesigner.Core.Properties;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace XDesigner.App.Controls;

/// <summary>
/// The properties window (§9.2). Local values come straight from the XAML, so the grid updates the moment an
/// edit happens; property metadata and effective values come from the renderer and fill in when they arrive.
/// Every edit goes through <see cref="DocumentViewModel.SetProperty"/> as one undo step.
/// </summary>
public sealed partial class PropertiesPane : UserControl
{
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(DocumentViewModel), typeof(PropertiesPane),
        new PropertyMetadata(null, (d, e) => ((PropertiesPane)d).OnDocumentChanged(e.OldValue as DocumentViewModel, e.NewValue as DocumentViewModel)));

    private readonly PropertyEditorContext _context;
    private readonly Dictionary<string, IReadOnlyList<PropertyInfoDto>> _properties = new(StringComparer.Ordinal);
    private readonly Dictionary<int, IReadOnlyDictionary<string, PropertyValueDto>> _values = [];
    private readonly Dictionary<int, int> _valuesFetched = [];
    private readonly Dictionary<string, Button> _categoryHeaders = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private Dictionary<string, PropertyRowView> _views = new(StringComparer.Ordinal);
    private IReadOnlyList<PropertyRow> _rows = [];
    private List<UIElement> _shown = [];
    private IReadOnlyList<string> _fonts = [];
    private bool _refreshQueued;
    private bool _valuesStale;
    private int _refreshToken;
    private int _valuesGeneration;

    // Name box: the element being renamed is fixed when typing starts (§9.2 header).
    private int? _nameTarget;
    private string _shownName = "";

    /// <summary>Typed but not committed (compared with the text shown: TextChanged arrives asynchronously).</summary>
    private bool NameDirty => NameBox.Text != _shownName;

    public PropertiesPane()
    {
        InitializeComponent();
        _context = new PropertyEditorContext
        {
            Commit = Commit,
            ReportProblem = ShowProblem,
            SelectElement = id => Document?.Selection.Select(id),
            FontFamilies = () => _fonts,
            ProjectDirectory = () => Document?.ProjectDirectory,
            Profile = () => Document?.Xaml.Profile,
            BrowseImageAsync = commit => Document is { } doc && BrowseImage is { } browse ? browse(doc, commit) : Task.CompletedTask,
        };
        ShowMessage("Open a file to see the selected control's properties.");
    }

    public DocumentViewModel? Document
    {
        get => (DocumentViewModel?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>Commits whatever is being typed (before the selection or document changes).</summary>
    public void FlushPendingEdits()
    {
        CommitName();
        FlushEventEdits();
        foreach (var view in _views.Values.Where(v => v.Editor.HasPendingEdit).ToList())
        {
            view.Editor.Flush();
        }
    }

    /// <summary>
    /// The document edits are committed to: normally <see cref="Document"/>, but while the pane flushes the edits
    /// typed for the previous document, that one (when this pane's callback runs, <see cref="Document"/> is already
    /// the new document, whose element ids would match unrelated elements).
    /// </summary>
    private DocumentViewModel? EditTarget => _flushTarget ?? Document;

    private DocumentViewModel? _flushTarget;

    private void OnDocumentChanged(DocumentViewModel? oldDocument, DocumentViewModel? newDocument)
    {
        if (oldDocument is not null)
        {
            _flushTarget = oldDocument;
            try
            {
                FlushPendingEdits();
            }
            finally
            {
                _flushTarget = null;
            }

            // A name the old document refused stays behind with that document's control id; ids are per document,
            // so it must not be committed to the new one (review H1).
            _nameTarget = null;
            NameBox.Text = "";
            _shownName = "";
            ShowProblem(null);

            oldDocument.Selection.Changed -= OnSelectionChanged;
            oldDocument.Xaml.Changed -= OnXamlChanged;
            oldDocument.PropertyChanged -= OnDocumentPropertyChanged;
        }

        _properties.Clear();
        _events.Clear();
        _values.Clear();
        _valuesFetched.Clear();
        _fonts = [];
        if (newDocument is not null)
        {
            newDocument.Selection.Changed += OnSelectionChanged;
            newDocument.Xaml.Changed += OnXamlChanged;
            newDocument.PropertyChanged += OnDocumentPropertyChanged;
        }

        ScheduleRefresh(values: true);
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        FlushPendingEdits(); // a value typed for the old selection goes to the old selection
        ShowProblem(null);
        ScheduleRefresh(values: false);
    }

    private void OnXamlChanged(object? sender, XamlDocumentChange e) => ScheduleRefresh(values: false);

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.Frame))
        {
            ScheduleRefresh(values: true); // a new render: effective values may have changed
        }
        else if (e.PropertyName == nameof(DocumentViewModel.Catalog))
        {
            // A new catalog means a new renderer (e.g. after a rebuild): custom controls may have new properties/events.
            _properties.Clear();
            _events.Clear();
            ScheduleRefresh(values: true);
        }
    }

    /// <summary>Coalesces bursts of changes (an edit changes the XAML, then a frame arrives) into one refresh.</summary>
    private void ScheduleRefresh(bool values)
    {
        _valuesStale |= values;
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _refreshQueued = false;
            _ = RefreshAsync();
        });
    }

    private async Task RefreshAsync()
    {
        var token = ++_refreshToken;
        var doc = Document;
        if (doc is null)
        {
            ShowHeader(null, []);
            ShowMessage("Open a file to see the selected control's properties.");
            return;
        }

        var ids = doc.Selection.Selected.Where(id => doc.Xaml.FindById(id) is not null).ToList();
        var nodes = ids.Select(id => doc.Xaml.FindById(id)!).ToList();
        ShowHeader(doc, nodes);
        if (nodes.Count == 0)
        {
            ShowMessage("Select a control on the form or in the outline to see its properties.");
            return;
        }

        if (_valuesStale)
        {
            // Keep showing the old values until the new ones arrive (no flicker); they're fetched again below.
            _valuesStale = false;
            _valuesGeneration++;
        }

        // Show what's known right away (local values are always current), then fill in from the renderer.
        Render(doc, ids);

        var changed = false;
        var missingTypes = nodes.Select(n => doc.Catalog.Resolve(n)?.FullName).OfType<string>().Distinct()
            .Where(t => !_properties.ContainsKey(t)).ToList();
        foreach (var type in missingTypes)
        {
            if (await doc.GetPropertiesAsync(type) is { } props)
            {
                _properties[type] = props;
                changed = true;
            }
        }

        var generation = _valuesGeneration;
        foreach (var id in ids.Where(id => _valuesFetched.GetValueOrDefault(id, -1) != generation).ToList())
        {
            // A newer frame may arrive while this is in flight: then these values are already out of date, drop them.
            if (await doc.GetValuesAsync(id) is { } values && generation == _valuesGeneration && ReferenceEquals(doc, Document))
            {
                _values[id] = values;
                _valuesFetched[id] = generation;
                changed = true;
            }
        }

        changed |= await LoadEventsAsync(doc, nodes);

        if (_fonts.Count == 0)
        {
            _fonts = await doc.GetFontFamiliesAsync();
        }

        if (changed && token == _refreshToken && ReferenceEquals(doc, Document))
        {
            Render(doc, ids);
        }
    }

    private void Render(DocumentViewModel doc, IReadOnlyList<int> ids)
    {
        RenderEvents(doc, ids);
        if (doc.Catalog.IsEmpty)
        {
            ShowMessage("Loading properties…");
            return;
        }

        var rows = PropertyGridBuilder.Build(doc.Xaml, doc.Catalog, ids, t => _properties.GetValueOrDefault(t), id => _values.GetValueOrDefault(id));
        if (rows.Count == 0)
        {
            ShowMessage(nodesKnown(doc, ids) ? "Loading properties…" : "The renderer doesn't know this control's type, so its properties can't be shown.");
            return;
        }

        var views = new Dictionary<string, PropertyRowView>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!_views.TryGetValue(row.Key, out var view) || view.EditorKind != PropertyRowView.EditorKindOf(row))
            {
                view = new PropertyRowView(row, _context);
            }

            view.Update(row, ids);
            views[row.Key] = view;
        }

        _views = views;
        _rows = rows;
        ApplyFilter();

        static bool nodesKnown(DocumentViewModel doc, IReadOnlyList<int> ids) =>
            ids.All(id => doc.Xaml.FindById(id) is { } n && doc.Catalog.Resolve(n) is not null);
    }

    /// <summary>Lays out the visible rows: by category (collapsible) or A–Z, filtered by search, set-only and advanced.</summary>
    private void ApplyFilter()
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var search = SearchBox.Text.Trim();
        var setOnly = SetOnlyToggle.IsChecked == true;
        var advanced = AdvancedToggle.IsChecked == true;
        var visible = _rows.Where(r =>
            (advanced || !r.IsAdvanced || r.IsSet)
            && (!setOnly || r.IsSet)
            && (search.Length == 0 || r.Key.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();

        var elements = new List<UIElement>();
        if (AlphabeticalToggle.IsChecked == true)
        {
            elements.AddRange(visible.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Select(r => (UIElement)_views[r.Key].View));
        }
        else
        {
            foreach (var group in visible.GroupBy(r => r.Category))
            {
                var collapsed = _collapsed.Contains(group.Key) && search.Length == 0;
                elements.Add(CategoryHeader(_categoryHeaders, _collapsed, group.Key, collapsed, ApplyFilter));
                if (!collapsed)
                {
                    elements.AddRange(group.Select(r => (UIElement)_views[r.Key].View));
                }
            }
        }

        RowsScroll.Visibility = Visibility.Visible;
        MessageText.Visibility = elements.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MessageText.Text = elements.Count == 0 ? "No properties match." : "";

        // Re-laying out moves focus, so only touch the panel when the visible rows actually changed.
        if (!elements.SequenceEqual(_shown))
        {
            RowsPanel.Children.Clear();
            foreach (var element in elements)
            {
                RowsPanel.Children.Add(element);
            }

            _shown = elements;
        }
    }

    private static Button CategoryHeader(Dictionary<string, Button> headers, HashSet<string> collapsedSet, string category, bool collapsed, Action reapply)
    {
        if (!headers.TryGetValue(category, out var header))
        {
            header = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 6, 8, 2),
            };
            header.Click += (_, _) =>
            {
                if (!collapsedSet.Remove(category))
                {
                    collapsedSet.Add(category);
                }

                reapply();
            };
            headers[category] = header;
        }

        header.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new FontIcon { Glyph = collapsed ? "" : "", FontSize = 10 },
                new TextBlock { Text = category, FontWeight = FontWeights.SemiBold },
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(header, $"{category} ({(collapsed ? "collapsed" : "expanded")})");
        return header;
    }

    private void ShowMessage(string message)
    {
        ShowEventsMessage(message.Replace("properties", "events", StringComparison.Ordinal));
        RowsPanel.Children.Clear();
        _shown = [];
        _rows = [];
        RowsScroll.Visibility = Visibility.Collapsed;
        MessageText.Visibility = Visibility.Visible;
        MessageText.Text = message;
    }

    private string? Commit(IReadOnlyList<int> targets, string key, string? value)
    {
        if (EditTarget is not { } doc)
        {
            return null;
        }

        var live = targets.Where(id => doc.Xaml.FindById(id) is not null).ToList();
        if (live.Count == 0)
        {
            return "That control is no longer in the document.";
        }

        try
        {
            doc.SetProperty(live, key, value);
            ScheduleRefresh(values: false); // also when nothing changed, so the box shows the normalized text
            return null;
        }
        catch (Exception ex)
        {
            return $"Couldn't set {key}: {ex.Message}";
        }
    }

    private void ShowProblem(string? problem)
    {
        ProblemBar.Message = problem ?? "";
        ProblemBar.IsOpen = problem is not null;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        ApplyFilter();
        ApplyEventFilter();
        if (ReferenceEquals(sender, AdvancedToggle))
        {
            ShowAdvancedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>F4: puts the keyboard in the Properties window (the name box), as in Visual Studio.</summary>
    public void FocusName() => NameBox.Focus(FocusState.Keyboard);

    /// <summary>Set by the main window: picks an image file and imports it into the document's project (§12).</summary>
    public Func<DocumentViewModel, Action<string>, Task>? BrowseImage { get; set; }

    /// <summary>"Show advanced" (remembered in Settings, §15.3).</summary>
    public bool ShowAdvanced
    {
        get => AdvancedToggle.IsChecked == true;
        set
        {
            if (ShowAdvanced != value)
            {
                AdvancedToggle.IsChecked = value;
                ApplyFilter();
                ApplyEventFilter();
            }
        }
    }

    /// <summary>The user turned "Show advanced" on or off.</summary>
    public event EventHandler? ShowAdvancedChanged;

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var events = sender.SelectedItem == EventsTab;
        PropertiesContent.Visibility = events ? Visibility.Collapsed : Visibility.Visible;
        EventsContent.Visibility = events ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.PlaceholderText = events ? "Search events" : "Search properties";
    }

    // ---- Name header ----

    private void ShowHeader(DocumentViewModel? doc, IReadOnlyList<ElementNode> nodes)
    {
        if (NameDirty && _nameTarget is not null)
        {
            return; // keep what is being typed
        }

        if (nodes.Count == 1)
        {
            var node = nodes[0];
            _nameTarget = node.Id;
            NameBox.IsEnabled = true;
            NameBox.Text = node.Name ?? "";
            NameBox.PlaceholderText = "(no name)";
            var type = doc?.Catalog.Resolve(node);
            TypeText.Text = node.TypeName;
            ToolTipService.SetToolTip(TypeText, type?.FullName ?? node.TypeName);
        }
        else
        {
            _nameTarget = null;
            NameBox.IsEnabled = false;
            NameBox.Text = "";
            NameBox.PlaceholderText = nodes.Count == 0 ? "" : $"{nodes.Count} controls selected";
            var types = nodes.Select(n => n.TypeName).Distinct().ToList();
            TypeText.Text = types.Count == 1 ? types[0] : "";
            ToolTipService.SetToolTip(TypeText, null);
        }

        _shownName = NameBox.Text;
    }

    private void NameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            CommitName();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape && NameDirty)
        {
            NameBox.Text = _shownName;
            ShowProblem(null);
            e.Handled = true;
        }
    }

    private void NameBox_LostFocus(object sender, RoutedEventArgs e) => CommitName(revertIfInvalid: true);

    private void CommitName(bool revertIfInvalid = false)
    {
        if (!NameDirty || EditTarget is not { } doc || _nameTarget is not { } id)
        {
            return;
        }

        var problem = doc.Rename(id, NameBox.Text);
        ShowProblem(problem);
        if (problem is null)
        {
            _shownName = NameBox.Text;
        }
        else if (revertIfInvalid)
        {
            NameBox.Text = _shownName;
        }

        ScheduleRefresh(values: false);
    }
}
