using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using XDesigner.App.ViewModels;
using XDesigner.Protocol.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace XDesigner.App.Controls;

/// <summary>
/// Shows one document's rendered frame (§8). The frame is rendered at exactly
/// <c>RasterizationScale × Zoom</c> pixels per DIP and displayed at <c>pixels ÷ RasterizationScale</c>
/// WinUI units, so every rendered pixel lands on one screen pixel (no resampling blur).
/// </summary>
public sealed partial class DesignSurface : UserControl
{
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(DocumentViewModel), typeof(DesignSurface),
        new PropertyMetadata(null, (d, e) => ((DesignSurface)d).OnDocumentChanged(e.OldValue as DocumentViewModel, e.NewValue as DocumentViewModel)));

    private WriteableBitmap? _bitmap;
    private double _rasterizationScale = 1;
    private RenderResult? _shown;

    public DesignSurface()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Scroller.AddHandler(PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Scroller_PointerWheelChanged), handledEventsToo: true);
    }

    public DocumentViewModel? Document
    {
        get => (DocumentViewModel?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        XamlRoot.Changed += OnXamlRootChanged;
        _rasterizationScale = XamlRoot.RasterizationScale;
        RequestRender();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is not null)
        {
            XamlRoot.Changed -= OnXamlRootChanged;
        }
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (Math.Abs(sender.RasterizationScale - _rasterizationScale) > 0.001)
        {
            _rasterizationScale = sender.RasterizationScale; // moved to a monitor with a different DPI
            RequestRender();
        }
    }

    private void OnDocumentChanged(DocumentViewModel? oldDocument, DocumentViewModel? newDocument)
    {
        if (oldDocument is not null)
        {
            oldDocument.PropertyChanged -= OnDocumentPropertyChanged;
            oldDocument.RenderRequested -= RequestRender;
            oldDocument.Selection.Changed -= OnSelectionChanged;
            oldDocument.FitRequested -= OnFitRequested;
            oldDocument.Undo.StateChanged -= OnUndoStateChanged;
        }

        _shown = null;
        _hoverId = null;
        if (newDocument is not null)
        {
            newDocument.PropertyChanged += OnDocumentPropertyChanged;
            newDocument.RenderRequested += RequestRender;
            newDocument.Selection.Changed += OnSelectionChanged;
            newDocument.FitRequested += OnFitRequested;
            newDocument.Undo.StateChanged += OnUndoStateChanged;
            ShowFrame(newDocument.Frame);
            UpdateStatus();
            if (IsLoaded)
            {
                RequestRender();
            }
        }
    }

    private void RequestRender()
    {
        if (Document is { } doc && XamlRoot is not null)
        {
            // Show the old frame at the new zoom right away; the sharp re-render replaces it.
            ApplyDisplaySize();
            doc.Render(_rasterizationScale);
        }
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DocumentViewModel.Frame):
                ShowFrame(Document?.Frame);
                break;
            case nameof(DocumentViewModel.Layout):
                RedrawAdorners();
                break;
            case nameof(DocumentViewModel.IsRendering):
            case nameof(DocumentViewModel.ErrorMessage):
            case nameof(DocumentViewModel.IsRendererError):
                UpdateStatus();
                break;
        }
    }

    private void ShowFrame(RenderResult? frame)
    {
        if (frame?.Bgra is null)
        {
            FrameHost.Visibility = Visibility.Collapsed;
            return;
        }

        if (_bitmap is null || _bitmap.PixelWidth != frame.PixelWidth || _bitmap.PixelHeight != frame.PixelHeight)
        {
            _bitmap = new WriteableBitmap(frame.PixelWidth, frame.PixelHeight);
            FrameImage.Source = _bitmap;
        }

        // Both sides use premultiplied BGRA8, so the bytes copy straight across.
        using (var stream = _bitmap.PixelBuffer.AsStream())
        {
            stream.Write(frame.Bgra, 0, frame.Bgra.Length);
        }

        _bitmap.Invalidate();
        _shown = frame;

        var isWindow = frame.RootKind == RootKind.Window;
        TitleBar.Visibility = isWindow ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = frame.WindowTitle ?? "";
        FrameHost.Visibility = Visibility.Visible;
        ApplyDisplaySize();
        UpdateStatus();
    }

    /// <summary>Sizes the image in WinUI units for the current zoom (exact once the matching render arrives).</summary>
    private void ApplyDisplaySize()
    {
        if (_shown is not { } frame || Document is not { } doc)
        {
            return;
        }

        var dipWidth = frame.PixelWidth / frame.Scale;
        var dipHeight = frame.PixelHeight / frame.Scale;
        FrameImage.Width = dipWidth * doc.Zoom;
        FrameImage.Height = dipHeight * doc.Zoom;
        TitleBar.Width = FrameImage.Width;
        RedrawAdorners();
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => RedrawAdorners();

    private void OnUndoStateChanged(object? sender, EventArgs e) => UpdateStatus();

    private void UpdateStatus()
    {
        var doc = Document;
        var hasFrame = _shown is not null;
        BusyBar.Visibility = doc?.IsRendering == true && hasFrame ? Visibility.Visible : Visibility.Collapsed;

        EmptyState.Visibility = hasFrame ? Visibility.Collapsed : Visibility.Visible;
        StartupRing.IsActive = !hasFrame && doc?.ErrorMessage is null;
        StartupRing.Visibility = StartupRing.IsActive ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = doc?.ErrorMessage is null ? "Starting the renderer…" : "Nothing to show yet.";

        ErrorBar.IsOpen = doc?.ErrorMessage is not null;
        ErrorBar.Message = doc?.ErrorMessage ?? "";
        RetryButton.Visibility = doc?.IsRendererError == true ? Visibility.Visible : Visibility.Collapsed;
        UndoLastButton.Visibility = doc?.Undo.CanUndo == true ? Visibility.Visible : Visibility.Collapsed;
        UndoLastButton.Content = doc?.Undo.CanUndo == true ? $"Undo {Lower(doc.Undo.UndoDescription)}" : "Undo";
        FrameImage.Opacity = doc?.ErrorMessage is not null ? 0.45 : 1.0; // last good frame, dimmed (§8.5)
    }

    private static string Lower(string? description) =>
        string.IsNullOrEmpty(description) ? "last change" : char.ToLowerInvariant(description[0]) + description[1..];

    private void RetryButton_Click(object sender, RoutedEventArgs e) => Document?.Retry();

    private void UndoLastButton_Click(object sender, RoutedEventArgs e)
    {
        if (Document is { } doc && doc.Undo.CanUndo)
        {
            doc.Undo.Undo();
            if (doc.IsRendererError)
            {
                doc.Retry(); // a hung or crashed renderer needs a fresh start for the undone version
            }
        }
    }

    /// <summary>View ▸ Fit: the zoom at which the whole frame (and its title bar) fits in the visible area.</summary>
    private void OnFitRequested(object? sender, EventArgs e)
    {
        if (_shown is not { } frame || Document is not { } doc || Scroller.ViewportWidth <= 0)
        {
            return;
        }

        var chrome = frame.RootKind == RootKind.Window ? TitleBar.Height : 0;
        var margin = (FrameHost.Margin.Left + 1) * 2; // the margin and the 1-DIP border on both sides
        var fit = Math.Min(
            (Scroller.ViewportWidth - margin) / (frame.PixelWidth / frame.Scale),
            (Scroller.ViewportHeight - margin - chrome) / (frame.PixelHeight / frame.Scale));
        doc.Zoom = Math.Clamp(Math.Floor(fit * 100) / 100, DocumentViewModel.ZoomLevels[0], DocumentViewModel.ZoomLevels[^1]);
    }

    /// <summary>Ctrl + mouse wheel zooms, a step per notch (the scroll viewer itself is set not to zoom).</summary>
    private void Scroller_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (Document is not { } doc || !e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
        {
            return;
        }

        var delta = e.GetCurrentPoint(Scroller).Properties.MouseWheelDelta;
        if (delta != 0)
        {
            doc.StepZoom(delta > 0 ? +1 : -1);
            e.Handled = true;
        }
    }
}
