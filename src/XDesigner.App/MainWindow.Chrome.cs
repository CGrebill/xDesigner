using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace XDesigner.App;

/// <summary>
/// Window chrome: app icon, a title bar drawn by the app so it shares the Mica backdrop with the rest of the
/// window, and opening maximized (restoring to a centered window).
/// </summary>
public sealed partial class MainWindow
{
    private void InitializeChrome()
    {
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon); // title bar, taskbar and Alt+Tab
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall; // 48 px: room for the menus and toolbar
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        MainMenu.SizeChanged += (_, _) => UpdateTitleBarRegions();
        TitleToolbar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        TitleText.SizeChanged += (_, _) => UpdateTitleBarRegions(); // a longer title moves the menus
        Activated += (_, e) => TitleText.Foreground = (Brush)Application.Current.Resources[
            e.WindowActivationState == WindowActivationState.Deactivated ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush"];

        if (Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (_, _) => UpdateCaptionButtonColors();
            root.Loaded += (_, _) =>
            {
                UpdateCaptionButtonColors();
                UpdateTitleBarRegions();
            };
        }

        PlaceOnScreen();
    }

    /// <summary>
    /// Opens maximized. The restored size is 90% of the work area (at most 1600×1000), centered, so un-maximizing
    /// gives a centered window rather than wherever Windows would put it.
    /// </summary>
    private void PlaceOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(1600, (int)(area.Width * 0.9));
        var height = Math.Min(1000, (int)(area.Height * 0.9));
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    /// <summary>The system draws the minimize/maximize/close buttons; transparent backgrounds let the Mica show through.</summary>
    private void UpdateCaptionButtonColors()
    {
        var dark = Content is FrameworkElement { ActualTheme: ElementTheme.Dark };
        var foreground = dark ? Colors.White : Colors.Black;
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(255, 150, 150, 150) : Color.FromArgb(255, 120, 120, 120);
        bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0);
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0);
        bar.ButtonPressedForegroundColor = foreground;
    }

    /// <summary>
    /// Keeps the title bar content clear of the caption buttons, and makes the menus and toolbar clickable: the whole
    /// title bar is a drag area except these passthrough rectangles (physical pixels).
    /// </summary>
    private void UpdateTitleBarRegions()
    {
        if (AppTitleBar.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        var scale = xamlRoot.RasterizationScale;
        CaptionButtonsColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);

        RectInt32 ToPixels(FrameworkElement element)
        {
            var bounds = element.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return new RectInt32((int)Math.Round(bounds.X * scale), (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale));
        }

        Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, [ToPixels(MainMenu), ToPixels(TitleToolbar)]);
    }
}
