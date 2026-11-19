using System.Collections.ObjectModel;
using XDesigner.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace XDesigner.App.Controls;

/// <summary>
/// Tree of the document's controls, kept in sync with the selection both ways (§9.6): picking a row
/// selects the element on the surface, and selecting on the surface highlights (and reveals) its row.
/// </summary>
public sealed partial class DocumentOutline : UserControl
{
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(DocumentViewModel), typeof(DocumentOutline),
        new PropertyMetadata(null, (d, e) => ((DocumentOutline)d).OnDocumentChanged(e.OldValue as DocumentViewModel, e.NewValue as DocumentViewModel)));

    private readonly ObservableCollection<OutlineNodeViewModel> _roots = [];
    private Dictionary<int, OutlineNodeViewModel> _byId = [];
    private bool _syncing;

    public DocumentOutline()
    {
        InitializeComponent();
        Tree.ItemsSource = _roots;

        // Drops land on the root grid, not the TreeView: with AllowDrop on, the TreeView's own drag handling scrolls the
        // tree whenever the pointer is in its lower half (and marks the events handled).
        Root.AddHandler(DragOverEvent, new DragEventHandler(Tree_DragOver), handledEventsToo: true);
        Root.AddHandler(DropEvent, new DragEventHandler(Tree_Drop), handledEventsToo: true);
        Tree.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Tree_PointerPressed), handledEventsToo: true);
        Tree.AddHandler(PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Tree_PointerMoved), handledEventsToo: true);
        Tree.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Tree_PointerReleased), handledEventsToo: true);
    }

    public DocumentViewModel? Document
    {
        get => (DocumentViewModel?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    private void OnDocumentChanged(DocumentViewModel? oldDocument, DocumentViewModel? newDocument)
    {
        if (oldDocument is not null)
        {
            oldDocument.StructureChanged -= OnStructureChanged;
            oldDocument.Selection.Changed -= OnSelectionChanged;
            oldDocument.Xaml.Changed -= OnXamlChanged;
            oldDocument.HiddenChanged -= OnHiddenChanged;
        }

        if (newDocument is not null)
        {
            newDocument.StructureChanged += OnStructureChanged;
            newDocument.Selection.Changed += OnSelectionChanged;
            newDocument.Xaml.Changed += OnXamlChanged;
            newDocument.HiddenChanged += OnHiddenChanged;
        }

        Rebuild();
    }

    /// <summary>
    /// After an edit, update the existing rows in place. Recreating them would make WinUI's TreeView build new
    /// containers, which come up collapsed regardless of <see cref="OutlineNodeViewModel.IsExpanded"/>.
    /// </summary>
    private void OnStructureChanged(object? sender, EventArgs e) => SyncRows(structureChanged: true);

    /// <summary>Renames (Properties window) and text edits change labels without changing the structure.</summary>
    private void OnXamlChanged(object? sender, XDesigner.Core.Xaml.XamlDocumentChange e)
    {
        if (!e.StructureChanged)
        {
            SyncRows(structureChanged: false);
        }
    }

    private void SyncRows(bool structureChanged)
    {
        if (Document is { } doc && _roots.FirstOrDefault() is { } root && root.Id == doc.Xaml.Root.Id)
        {
            if (structureChanged)
            {
                // A move (Wrap in, outline drag) can take away the highlighted row; TreeView would keep that stale row as
                // its selection and echo it back later, overriding every new selection. Clear it first.
                Select(null);
            }

            root.Sync(doc.Xaml.Root);
            _byId = root.DescendantsAndSelf().ToDictionary(n => n.Id);
            ShowHidden();
            if (structureChanged)
            {
                ExpandRows(root); // rows put back by a reorder come up collapsed
                ShowSelection();
            }
        }
        else
        {
            Rebuild();
        }
    }

    /// <summary>The eye icons follow the document's hidden-on-the-form set.</summary>
    private void ShowHidden()
    {
        if (Document is not { } doc)
        {
            return;
        }

        foreach (var node in _byId.Values)
        {
            node.IsHiddenInDesigner = doc.Preview.IsHidden(node.Id);
        }
    }

    private void OnHiddenChanged(object? sender, EventArgs e) => ShowHidden();

    private void Eye_Click(object sender, RoutedEventArgs e)
    {
        if (Document is { } doc && (sender as FrameworkElement)?.DataContext is OutlineNodeViewModel { CanHide: true } node)
        {
            doc.SetHidden(node.Id, !node.IsHiddenInDesigner);
        }
    }

    private void Rebuild()
    {
        _roots.Clear();
        _byId = [];
        if (Document is { } doc)
        {
            var root = new OutlineNodeViewModel(doc.Xaml.Root, null);
            _roots.Add(root);
            _byId = root.DescendantsAndSelf().ToDictionary(n => n.Id);
            ShowHidden();
            ExpandRows(root);
        }

        EmptyText.Visibility = Document is null ? Visibility.Visible : Visibility.Collapsed;
        ShowSelection();
    }

    /// <summary>
    /// When the whole tree is replaced (a file reloaded from disk), TreeView reuses rows that ignore the bound
    /// IsExpanded and stay collapsed. Expand the realized rows directly, a level per pass: child rows only exist once
    /// their parent row is open.
    /// </summary>
    /// <remarks>
    /// Runs on a short timer rather than a few idle passes: when the window is busy (a reload while the renderer starts),
    /// the rows can take longer than a few dispatcher turns to appear, and the tree stayed collapsed.
    /// Stops once a few ticks in a row find nothing to open, or after about a second and a half.
    /// </remarks>
    private void ExpandRows(OutlineNodeViewModel root)
    {
        _expandTimer?.Stop();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        var ticks = 0;
        var quiet = 0;
        timer.Tick += (_, _) =>
        {
            ticks++;
            var changed = false;
            if (_roots.FirstOrDefault() == root)
            {
                foreach (var node in root.DescendantsAndSelf().Where(n => n.IsExpanded && n.Children.Count > 0))
                {
                    if (Tree.ContainerFromItem(node) is TreeViewItem { IsExpanded: false } row)
                    {
                        row.IsExpanded = true;
                        changed = true;
                    }
                }
            }

            quiet = changed ? 0 : quiet + 1;
            if (_roots.FirstOrDefault() != root || ticks >= 30 || (quiet >= 4 && ticks >= 6))
            {
                timer.Stop();
            }
        };
        _expandTimer = timer;
        timer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _expandTimer;

    private void OnSelectionChanged(object? sender, EventArgs e) => ShowSelection();

    /// <summary>Highlights the primary selection's row, expanding its ancestors first so it has a container.</summary>
    private void ShowSelection()
    {
        if (Document?.Selection.Primary is not { } id || !_byId.TryGetValue(id, out var node))
        {
            Select(null);
            return;
        }

        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            parent.IsExpanded = true;
        }

        // Containers for newly expanded rows exist after the next layout pass.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Document?.Selection.Primary == id)
            {
                Select(node);
            }
        });
    }

    private void Select(OutlineNodeViewModel? node)
    {
        _syncing = true;
        try
        {
            Tree.SelectedItem = node;
            if (node is not null && Tree.ContainerFromItem(node) is TreeViewItem container)
            {
                container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    // IsExpanded is bound one way (view model → row): a two-way binding let newly created rows, which TreeView makes
    // collapsed, write "collapsed" back (seen when a reloaded file replaced the whole tree). The user's own
    // expanding and collapsing comes back through these events instead.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "XAML event handlers are instance methods.")]
    private void Tree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is OutlineNodeViewModel node)
        {
            node.IsExpanded = true;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "XAML event handlers are instance methods.")]
    private void Tree_Collapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        if (args.Item is OutlineNodeViewModel node)
        {
            node.IsExpanded = false;
        }
    }

    /// <summary>Clicks and arrow keys in the tree select on the surface.</summary>
    private void Tree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        // TreeView can raise this after a programmatic highlight has finished (outside _syncing); a row that is
        // already the primary selection is that echo, and must not collapse a multi-selection to one element.
        if (!_syncing && args.AddedItems.FirstOrDefault() is OutlineNodeViewModel node && Document?.Selection.Primary != node.Id)
        {
            Document?.Selection.Select(node.Id);
        }
    }
}
