using XDesigner.App.Services;
using XDesigner.Core.Settings;
using XDesigner.Protocol.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace XDesigner.App;

/// <summary>Settings (§15.3): the dialog, recent files (File menu and start page), pane sizes, snapping and "advanced".</summary>
public sealed partial class MainWindow
{
    private void InitializeSettings()
    {
        var settings = ViewModel.Settings;
        DesignerOptions.Apply(settings);
        GridSnapToggle.IsChecked = settings.SnapToGrid;
        Properties.ShowAdvanced = settings.ShowAdvancedProperties;
        Properties.ShowAdvancedChanged += (_, _) =>
        {
            ViewModel.Settings.ShowAdvancedProperties = Properties.ShowAdvanced;
            ViewModel.SaveSettings();
        };

        if (settings.ToolboxWidth is > 100 and < 1000 and var toolbox)
        {
            ToolboxColumn.Width = new GridLength(toolbox);
            _toolboxWidth = toolbox;
        }

        if (settings.SidePaneWidth is > 150 and < 1200 and var side)
        {
            SideColumn.Width = new GridLength(side);
        }

        if (settings.OutputHeight is > 40 and < 1200 and var output)
        {
            OutputRow.Height = new GridLength(output);
            _outputHeight = output;
        }

        ShowToolboxMenuItem.IsChecked = settings.ShowToolbox;
        ShowOutputMenuItem.IsChecked = settings.ShowOutput;
        ApplyPaneVisibility();

        Closed += (_, _) => SavePaneSizes();
        ViewModel.RecentChanged += (_, _) => RebuildRecent();
        ViewModel.SettingsChanged += (_, _) =>
        {
            DesignerOptions.Apply(ViewModel.Settings);
            GridSnapToggle.IsChecked = ViewModel.Settings.SnapToGrid;
            Properties.ShowAdvanced = ViewModel.Settings.ShowAdvancedProperties;
        };
        RebuildRecent();

        AddAccelerator((VirtualKey)188, () => Settings_Click(this, new RoutedEventArgs())); // Ctrl+,
    }

    private void SavePaneSizes()
    {
        var settings = ViewModel.Settings;
        RememberPaneSizes();
        settings.ToolboxWidth = _toolboxWidth;
        settings.SidePaneWidth = SideColumn.ActualWidth;
        settings.OutputHeight = _outputHeight;
        settings.ShowToolbox = ShowToolboxMenuItem.IsChecked;
        settings.ShowOutput = ShowOutputMenuItem.IsChecked;
        ViewModel.SaveSettings();
    }

    // Sizes of the panes to restore when they're shown again (a hidden pane's column or row is 0).
    private double _toolboxWidth = 220;
    private double _outputHeight = 160;

    private void RememberPaneSizes()
    {
        if (ToolboxPaneBorder.Visibility == Visibility.Visible && ToolboxColumn.ActualWidth > 0)
        {
            _toolboxWidth = ToolboxColumn.ActualWidth;
        }

        if (OutputPaneBorder.Visibility == Visibility.Visible && OutputRow.ActualHeight > 0)
        {
            _outputHeight = OutputRow.ActualHeight;
        }
    }

    /// <summary>View ▸ Toolbox / Output: shows or hides a pane, keeping its size for when it comes back.</summary>
    private void ApplyPaneVisibility()
    {
        RememberPaneSizes();

        var toolbox = ShowToolboxMenuItem.IsChecked;
        ToolboxColumn.MinWidth = toolbox ? 140 : 0;
        ToolboxColumn.Width = new GridLength(toolbox ? _toolboxWidth : 0);
        ToolboxPaneBorder.Visibility = ToolboxSplitter.Visibility = toolbox ? Visibility.Visible : Visibility.Collapsed;

        var output = ShowOutputMenuItem.IsChecked;
        OutputRow.Height = new GridLength(output ? _outputHeight : 0);
        OutputPaneBorder.Visibility = OutputSplitter.Visibility = output ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TogglePane_Click(object sender, RoutedEventArgs e)
    {
        ApplyPaneVisibility();
        SavePaneSizes();
    }

    private void ShowSolutionExplorer_Click(object sender, RoutedEventArgs e)
    {
        SideTabs.SelectedItem = SolutionTab;
        SolutionPane.FocusTree(); // keyboard users land in it (review L51)
    }

    /// <summary>
    /// Ctrl+Alt+L is AltGr+L on many keyboards (ł in Polish): while typing in a box, the key is the character, not the
    /// shortcut (review L50).
    /// </summary>
    private void SolutionAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or AutoSuggestBox or PasswordBox or RichEditBox)
        {
            args.Handled = true; // no pane switch; the box gets its character
            return;
        }

        ShowSolutionExplorer_Click(sender, new RoutedEventArgs());
        args.Handled = true;
    }

    private void ShowErrorList_Click(object sender, RoutedEventArgs e) => ShowOutputTab(ErrorsTab);

    /// <summary>Brings an Output tab forward, showing the Output pane first if View ▸ Output hid it (review M32).</summary>
    private void ShowOutputTab(SelectorBarItem tab)
    {
        if (!ShowOutputMenuItem.IsChecked)
        {
            ShowOutputMenuItem.IsChecked = true;
            ApplyPaneVisibility();
        }

        OutputTabs.SelectedItem = tab;
    }

    private void GridSnapToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Settings.SnapToGrid = GridSnapToggle.IsChecked == true;
        ViewModel.ApplySettings();
    }

    /// <summary>File ▸ Open recent and the start page list.</summary>
    private void RebuildRecent()
    {
        var recent = ViewModel.Settings.Recent;
        RecentMenu.Items.Clear();
        RecentList.Children.Clear();
        foreach (var path in recent)
        {
            var item = new MenuFlyoutItem { Text = $"{Path.GetFileName(path)}  —  {Path.GetDirectoryName(path)}" };
            ToolTipService.SetToolTip(item, path);
            item.Click += async (_, _) => await ViewModel.OpenRecentAsync(path);
            RecentMenu.Items.Add(item);

            if (RecentList.Children.Count < 6)
            {
                var link = new HyperlinkButton
                {
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            new FontIcon { Glyph = Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".sln" or ".slnx" ? "" : "", FontSize = 14 },
                            new TextBlock { Text = Path.GetFileName(path) },
                            new TextBlock { Text = Path.GetDirectoryName(path), Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], MaxWidth = 380, TextTrimming = TextTrimming.CharacterEllipsis },
                        },
                    },
                };
                ToolTipService.SetToolTip(link, path);
                link.Click += async (_, _) => await ViewModel.OpenRecentAsync(path);
                RecentList.Children.Add(link);
            }
        }

        if (recent.Count > 0)
        {
            RecentMenu.Items.Add(new MenuFlyoutSeparator());
            var clear = new MenuFlyoutItem { Text = "Clear the list" };
            clear.Click += (_, _) =>
            {
                ViewModel.ChangeRecent(s => s.Recent.Clear());
                RebuildRecent();
            };
            RecentMenu.Items.Add(clear);
        }

        RecentMenu.IsEnabled = recent.Count > 0;
        RecentPanel.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settings = ViewModel.Settings;
        string[] editors = ["Automatic (VS Code if installed, otherwise the Windows default)", "Visual Studio Code", "Visual Studio", "The program Windows opens .cs files with", "Custom command"];
        var editor = new ComboBox { Header = "Open code in", ItemsSource = editors, SelectedIndex = (int)settings.Editor, MinWidth = 360 };
        var command = new TextBox
        {
            Header = "Custom command ({file} and {line} are filled in)",
            Text = settings.CustomEditorCommand,
            PlaceholderText = "notepad++.exe -n{line} \"{file}\"",
            IsEnabled = settings.Editor == EditorChoice.Custom,
        };
        editor.SelectionChanged += (_, _) => command.IsEnabled = editor.SelectedIndex == (int)EditorChoice.Custom;

        var theme = new ComboBox { Header = "Preview theme for files you open", ItemsSource = new[] { "Default", "Light", "Dark" }, SelectedIndex = (int)settings.PreviewTheme, MinWidth = 200 };
        var snapControls = new ToggleSwitch { Header = "Snap to other controls while moving and resizing (hold Alt to turn off for one drag)", IsOn = settings.SnapToControls };
        var snapGrid = new ToggleSwitch { Header = "Snap to a grid", IsOn = settings.SnapToGrid };
        var gridSize = new NumberBox { Header = "Grid size", Value = settings.GridSize, Minimum = 2, Maximum = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        var advanced = new ToggleSwitch { Header = "Show advanced properties and events", IsOn = settings.ShowAdvancedProperties };

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Settings",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new ScrollViewer
            {
                MaxHeight = 560,
                Content = new StackPanel
                {
                    Spacing = 14,
                    Children =
                    {
                        Section("Code editor"), editor, command,
                        Section("Preview"), theme,
                        Section("Snapping"), snapControls, snapGrid, gridSize,
                        Section("Properties window"), advanced,
                        new TextBlock
                        {
                            Text = $"Saved in {ViewModel.SettingsPath}",
                            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                },
            },
        };
        dialog.Resources["ContentDialogMaxWidth"] = 640d;

        if (await dialog.ShowOnceAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        settings.Editor = (EditorChoice)Math.Max(0, editor.SelectedIndex);
        settings.CustomEditorCommand = command.Text.Trim();
        settings.PreviewTheme = (ThemeVariantKind)Math.Max(0, theme.SelectedIndex);
        settings.SnapToControls = snapControls.IsOn;
        settings.SnapToGrid = snapGrid.IsOn;
        settings.GridSize = double.IsNaN(gridSize.Value) ? 8 : Math.Clamp(Math.Round(gridSize.Value), 2, 100);
        settings.ShowAdvancedProperties = advanced.IsOn;
        ViewModel.ApplySettings();
    }

    private static TextBlock Section(string text) =>
        new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 6, 0, -6) };
}
