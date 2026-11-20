using XDesigner.App.Services;
using System.Globalization;
using XDesigner.App.ViewModels;
using XDesigner.Protocol.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace XDesigner.App;

/// <summary>Preview size and zoom (§8.4): the Size box, Keep this preview size, Zoom to fit, Ctrl+wheel/keys.</summary>
public sealed partial class MainWindow
{
    /// <summary>Preview sizes offered in the Size box, after "Design size" (null) and before "Custom…".</summary>
    private static readonly (string Label, SizeDto Size)[] SizePresets =
    [
        ("1280 × 720", new SizeDto(1280, 720)),
        ("1024 × 768", new SizeDto(1024, 768)),
        ("800 × 600", new SizeDto(800, 600)),
        ("400 × 700 (narrow)", new SizeDto(400, 700)),
    ];

    private const string DesignSizeLabel = "Design size";
    private const string CustomLabel = "Custom…";

    private void InitializePreview()
    {
        FillSizeBox(null);

        // Ctrl+= / Ctrl+- on the main keyboard (the menu's accelerators cover the numeric keypad), Ctrl+9 = fit.
        AddAccelerator((VirtualKey)187, () => ViewModel.ZoomInCommand.Execute(null));
        AddAccelerator((VirtualKey)189, () => ViewModel.ZoomOutCommand.Execute(null));
        AddAccelerator(VirtualKey.Number9, () => ViewModel.ZoomToFitCommand.Execute(null));
        AddAccelerator(VirtualKey.F4, () => Properties.FocusName(), VirtualKeyModifiers.None); // like Visual Studio's Properties window
    }

    private void AddAccelerator(VirtualKey key, Action action, VirtualKeyModifiers modifiers = VirtualKeyModifiers.Control)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        Root.KeyboardAccelerators.Add(accelerator);
    }

    /// <summary>Design size, the presets, the document's custom size (if it has one) and Custom….</summary>
    private void FillSizeBox(SizeDto? current)
    {
        SizeBox.Items.Clear();
        SizeBox.Items.Add(DesignSizeLabel);
        foreach (var (label, _) in SizePresets)
        {
            SizeBox.Items.Add(label);
        }

        var preset = Array.FindIndex(SizePresets, p => p.Size == current);
        if (current is not null && preset < 0)
        {
            SizeBox.Items.Add(FormatSize(current));
        }

        SizeBox.Items.Add(CustomLabel);
        SizeBox.SelectedIndex = current is null ? 0 : preset >= 0 ? preset + 1 : SizePresets.Length + 1;
    }

    private static string FormatSize(SizeDto size) =>
        string.Create(CultureInfo.CurrentCulture, $"{size.Width:0} × {size.Height:0}");

    /// <summary>Called from <see cref="SyncFromSelectedDocument"/> (with the toolbar sync flag set).</summary>
    private void SyncPreview(DocumentViewModel? doc)
    {
        SizeBox.IsEnabled = doc is not null;
        FillSizeBox(doc?.PreviewSize);
        KeepSizeMenuItem.IsEnabled = doc?.PreviewSize is not null;
        ShowHiddenMenuItem.IsEnabled = doc?.Preview.Hidden.Count > 0;
    }

    private async void SizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingToolbar || ViewModel.SelectedDocument is not { } doc || SizeBox.SelectedItem is not string label)
        {
            return;
        }

        if (label == CustomLabel)
        {
            var custom = await AskSizeAsync(doc.PreviewSize ?? CurrentFrameSize(doc));
            if (custom is not null)
            {
                doc.PreviewSize = custom;
            }

            _syncingToolbar = true;
            SyncPreview(doc); // shows the new size, or puts the box back when cancelled
            _syncingToolbar = false;
            return;
        }

        doc.PreviewSize = label == DesignSizeLabel ? null
            : Array.Find(SizePresets, p => p.Label == label) is { Label: not null } preset ? preset.Size
            : doc.PreviewSize; // the custom entry: unchanged
    }

    private static SizeDto CurrentFrameSize(DocumentViewModel doc) =>
        doc.Frame is { } f ? new SizeDto(Math.Round(f.PixelWidth / f.Scale), Math.Round(f.PixelHeight / f.Scale)) : new SizeDto(800, 450);

    /// <summary>The Custom… dialog: width and height in DIPs.</summary>
    private async Task<SizeDto?> AskSizeAsync(SizeDto start)
    {
        var width = new NumberBox { Header = "Width", Value = start.Width, Minimum = 50, Maximum = 10000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 140 };
        var height = new NumberBox { Header = "Height", Value = start.Height, Minimum = 50, Maximum = 10000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 140 };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Preview size",
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Shows the form at this size (in device-independent pixels). The file doesn't change.", TextWrapping = TextWrapping.Wrap, MaxWidth = 320 },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { width, height } },
                },
            },
        };

        if (await dialog.ShowOnceAsync() != ContentDialogResult.Primary || double.IsNaN(width.Value) || double.IsNaN(height.Value))
        {
            return null;
        }

        return new SizeDto(Math.Clamp(Math.Round(width.Value), 50, 10000), Math.Clamp(Math.Round(height.Value), 50, 10000));
    }

    private void KeepSize_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedDocument is not { PreviewSize: { } size } doc)
        {
            return;
        }

        var isWindow = doc.Frame?.RootKind == RootKind.Window && doc.Xaml.Profile.WindowSizeInXaml;
        doc.Actions.SetDesignSize(size.Width, size.Height, isWindow);
        doc.PreviewSize = null;
        ShowNotification(NotifyKind.Success, isWindow
            ? $"{doc.Title} now opens at {FormatSize(size)} (Width and Height)."
            : $"{doc.Title} is now designed at {FormatSize(size)} (d:DesignWidth and d:DesignHeight, used only by designers).");
    }

    private void ShowHidden_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedDocument is { } doc)
        {
            foreach (var id in doc.Preview.Hidden.ToList())
            {
                doc.SetHidden(id, false);
            }
        }
    }
}
