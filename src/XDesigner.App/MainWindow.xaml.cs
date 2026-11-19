using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using XDesigner.App.Controls;
using XDesigner.App.ViewModels;
using XDesigner.Protocol.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace XDesigner.App;

/// <summary>The designer's main window (§15.1).</summary>
public sealed partial class MainWindow : Window
{
    private bool _syncingToolbar;

    public MainWindow(ShellViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        InitializeChrome();

        foreach (var zoom in DocumentViewModel.ZoomLevels)
        {
            ZoomBox.Items.Add(FormatZoom(zoom));
        }

        ZoomBox.Items.Add(FitLabel);

        ViewModel.Documents.CollectionChanged += OnDocumentsChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.OutputLines.CollectionChanged += OnOutputChanged;
        ViewModel.EventLines.CollectionChanged += OnEventLinesChanged;
        ViewModel.HandlerReported += ShowHandlerOutcome;
        InitializeProject();
        DesignCommands.Host = this;

        Root.AllowDrop = true;
        Root.DragOver += Root_DragOver;
        Root.Drop += Root_Drop;

        InitializePreview();
        InitializeSettings();
        InitializeImages();
        SyncFromSelectedDocument();
        InitializeEditing();
    }

    private const string FitLabel = "Fit";

    public ShellViewModel ViewModel { get; }

    private DocumentViewModel? _observedDocument;

    private static string FormatZoom(double zoom) => zoom.ToString("P0", CultureInfo.CurrentCulture);

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var type in new[] { ".axaml", ".xaml", ".csproj", ".sln", ".slnx" })
        {
            picker.FileTypeFilter.Add(type);
        }
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await ViewModel.OpenAnyAsync(file.Path);
        }
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncFromSelectedDocument();

    private void OnDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        NoDocuments.Visibility = ViewModel.Documents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SelectedDocument))
        {
            SyncFromSelectedDocument();
        }
    }

    private void OnOutputChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel.OutputLines.Count > 0)
        {
            OutputList.ScrollIntoView(ViewModel.OutputLines[^1]);
        }
    }

    /// <summary>A new Events entry brings the Events tab forward, so every handler change is visible (D9).</summary>
    private void OnEventLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel.EventLines.Count > 0)
        {
            OutputTabs.SelectedItem = EventsLogTab;
            EventsList.ScrollIntoView(ViewModel.EventLines[^1]);
        }
    }

    private void OutputTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        OutputList.Visibility = sender.SelectedItem == OutputTab ? Visibility.Visible : Visibility.Collapsed;
        BuildList.Visibility = sender.SelectedItem == BuildTab ? Visibility.Visible : Visibility.Collapsed;
        RunList.Visibility = sender.SelectedItem == RunTab ? Visibility.Visible : Visibility.Collapsed;
        EventsList.Visibility = sender.SelectedItem == EventsLogTab ? Visibility.Visible : Visibility.Collapsed;
        ErrorsPanel.Visibility = sender.SelectedItem == ErrorsTab ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SideTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var solution = sender.SelectedItem == SolutionTab;
        SolutionPane.Visibility = solution ? Visibility.Visible : Visibility.Collapsed;
        OutlinePane.Visibility = solution ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ErrorsList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e) => GoToDiagnostic();

    private void ErrorsList_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            GoToDiagnostic();
        }
    }

    /// <summary>Opens the selected error's file: XAML in the designer, anything else in the code editor at the line.</summary>
    private void GoToDiagnostic()
    {
        if (ErrorsList.SelectedItem is not ViewModels.DiagnosticRow { Diagnostic.File: { } file } row || !File.Exists(file))
        {
            return;
        }

        if (file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            _ = ViewModel.OpenFileAsync(file);
        }
        else if (ViewModel.CodeEditor.Open(file, row.Diagnostic.Line ?? 1) is { } problem)
        {
            ShowNotification(NotifyKind.Warning, problem);
        }
    }

    /// <summary>Toolbar and status bar reflect the selected document's zoom/theme.</summary>
    private void SyncFromSelectedDocument()
    {
        if (_observedDocument is not null)
        {
            _observedDocument.PropertyChanged -= OnSelectedDocumentPropertyChanged;
            _observedDocument.Selection.Changed -= OnSelectionChanged;
            _observedDocument.HiddenChanged -= OnHiddenChanged;
        }

        _observedDocument = ViewModel.SelectedDocument;
        if (_observedDocument is not null)
        {
            _observedDocument.PropertyChanged += OnSelectedDocumentPropertyChanged;
            _observedDocument.Selection.Changed += OnSelectionChanged;
            _observedDocument.HiddenChanged += OnHiddenChanged;
        }

        UpdateSelectionStatus();
        if (Toolbox is not null)
        {
            UpdateToolbox();
            UpdateEditState();
        }

        var doc = _observedDocument;
        _syncingToolbar = true;
        ThemeBox.IsEnabled = ZoomBox.IsEnabled = doc is not null;
        ThemeBox.SelectedIndex = doc is null ? 0 : (int)doc.Theme;
        var level = Array.IndexOf(DocumentViewModel.ZoomLevels, doc?.Zoom ?? 1d);
        ZoomBox.SelectedIndex = level >= 0 ? level : DocumentViewModel.ZoomLevels.Length; // between levels: "Fit"
        SyncPreview(doc);
        _syncingToolbar = false;

        StatusPath.Text = doc?.FilePath ?? "";
        StatusZoom.Text = doc is null ? "" : FormatZoom(doc.Zoom);
        // The document's own project (a solution may have several); without a document, the solution's name.
        var projectName = doc is not null ? ViewModel.ProjectOf(doc.FilePath)?.Info.Name ?? ViewModel.Project?.Name
            : ViewModel.Solution?.Name ?? ViewModel.Project?.Name;
        var project = projectName is not null ? $" – {projectName}" : "";
        Title = doc is null ? $"xDesigner{project}" : $"{doc.Title}{project} – xDesigner";
        TitleText.Text = Title;
        NoDocuments.Visibility = ViewModel.Documents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSelectedDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.Zoom) or nameof(DocumentViewModel.Theme) or nameof(DocumentViewModel.PreviewSize))
        {
            SyncFromSelectedDocument();
        }
        else if (e.PropertyName is nameof(DocumentViewModel.Layout))
        {
            UpdateSelectionStatus();
        }
        else if (e.PropertyName is nameof(DocumentViewModel.Catalog))
        {
            UpdateToolbox();
        }
        else if (e.PropertyName is nameof(DocumentViewModel.IsDirty))
        {
            UpdateEditState();
        }
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => UpdateSelectionStatus();

    private void OnHiddenChanged(object? sender, EventArgs e) =>
        ShowHiddenMenuItem.IsEnabled = _observedDocument?.Preview.Hidden.Count > 0;

    /// <summary>"button1 (Button) · 16, 374 · 75 × 30" for the primary selection, plus a count when several are selected.</summary>
    private void UpdateSelectionStatus()
    {
        var doc = _observedDocument;
        if (doc?.Selection.Primary is not { } id || doc.Xaml.FindById(id) is not { } node)
        {
            StatusSelection.Text = "";
            return;
        }

        var text = node.Name is null ? node.TypeName : $"{node.Name} ({node.TypeName})";
        if (doc.Layout.Find(id) is { } layout)
        {
            var b = layout.Bounds;
            text += string.Create(CultureInfo.CurrentCulture, $" · {b.X:0.#}, {b.Y:0.#} · {b.Width:0.#} × {b.Height:0.#}");
        }

        var count = doc.Selection.Selected.Count;
        StatusSelection.Text = count > 1 ? $"{count} selected · {text}" : text;
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingToolbar && ViewModel.SelectedDocument is { } doc && ThemeBox.SelectedIndex >= 0)
        {
            doc.Theme = (ThemeVariantKind)ThemeBox.SelectedIndex;
        }
    }

    private void ZoomBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingToolbar && ViewModel.SelectedDocument is { } doc && ZoomBox.SelectedIndex >= 0)
        {
            if (ZoomBox.SelectedIndex < DocumentViewModel.ZoomLevels.Length)
            {
                doc.Zoom = DocumentViewModel.ZoomLevels[ZoomBox.SelectedIndex];
            }
            else
            {
                doc.RequestFit();
            }
        }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.Caption = "Open";
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        foreach (var item in await e.DataView.GetStorageItemsAsync())
        {
            if (item is StorageFile file && file.FileType.ToLowerInvariant() is ".axaml" or ".xaml" or ".csproj" or ".sln" or ".slnx")
            {
                await ViewModel.OpenAnyAsync(file.Path);
            }
        }
    }
}
