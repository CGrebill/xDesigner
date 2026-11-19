using XDesigner.App.Services;
using XDesigner.Core.Project;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace XDesigner.App;

/// <summary>New project wizard, Add window / user control, Run and Stop (§11.3–11.5).</summary>
public sealed partial class MainWindow
{
    private static string DefaultProjectsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Avalonia Projects");

    private string? _lastProjectsFolder;

    private void UpdateRunState()
    {
        var project = ViewModel.Project;
        var running = ViewModel.IsRunning;
        RunButton.IsEnabled = RunMenuItem.IsEnabled = project is not null && !ViewModel.IsBuilding;
        RunButtonText.Text = running ? "Restart" : "Run";
        ToolTipService.SetToolTip(RunButton, running ? "Stop, rebuild and run again (F5)" : "Save, build and run the project (F5)");
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StopMenuItem.IsEnabled = running;
        AddWindowMenuItem.IsEnabled = AddUserControlMenuItem.IsEnabled = project is not null;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        Properties.FlushPendingEdits(); // Run saves the documents: include a value still being typed (§9.2)
        await ViewModel.RunAsync();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => ViewModel.StopRun();

    /// <summary>File ▸ New project: name, folder and window title; the project is created, opened and built.</summary>
    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { Header = "Project name", PlaceholderText = "HelloAvalonia", Text = NextFreeName(_lastProjectsFolder ?? DefaultProjectsFolder) };
        var location = new TextBox { Header = "Location", Text = _lastProjectsFolder ?? DefaultProjectsFolder, MinWidth = 360 };
        var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        var title = new TextBox { Header = "Window title", PlaceholderText = "Same as the project name" };
        var frameworks = ProjectTemplates.CreatableFrameworks;
        var framework = new ComboBox
        {
            Header = "Framework",
            ItemsSource = frameworks.Select(p => $"{p.DisplayName} ({p.TemplateVersion})").ToList(),
            SelectedIndex = 0,
            MinWidth = 220,
        };
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void Describe() =>
            description.Text = $"A desktop app for Windows, macOS and Linux ({frameworks[Math.Max(0, framework.SelectedIndex)].DisplayName}, .NET 10) with an empty main window.";
        framework.SelectionChanged += (_, _) => Describe();
        Describe();
        var where = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["NotifyErrorIcon"] };

        var locationRow = new Grid { ColumnSpacing = 8 };
        locationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        locationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(browse, 1);
        locationRow.Children.Add(location);
        locationRow.Children.Add(browse);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "New project",
            Content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 440,
                Children = { framework, description, name, locationRow, title, where, problem },
            },
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        void Validate()
        {
            var error = ProjectTemplates.ValidateProjectName(name.Text.Trim());
            var target = Path.Combine(location.Text.Trim(), name.Text.Trim());
            if (error is null && string.IsNullOrWhiteSpace(location.Text))
            {
                error = "Choose a folder for the project.";
            }
            else if (error is null && Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            {
                error = $"{target} already exists and isn't empty.";
            }

            where.Text = error is null ? $"Creates {Path.Combine(target, name.Text.Trim() + ".csproj")}" : "";
            problem.Text = error ?? "";
            dialog.IsPrimaryButtonEnabled = error is null;
        }

        name.TextChanged += (_, _) => Validate();
        location.TextChanged += (_, _) => Validate();
        browse.Click += async (_, _) =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSingleFolderAsync() is { } folder)
            {
                location.Text = folder.Path;
            }
        };
        Validate();

        if (await dialog.ShowOnceAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _lastProjectsFolder = location.Text.Trim();
        Directory.CreateDirectory(_lastProjectsFolder);
        if (await ViewModel.CreateProjectAsync(_lastProjectsFolder, name.Text.Trim(), title.Text, frameworks[Math.Max(0, framework.SelectedIndex)]) is { } failed)
        {
            ShowNotification(NotifyKind.Error, $"Couldn't create the project: {failed}");
        }
    }

    private static string NextFreeName(string folder)
    {
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? "HelloAvalonia" : $"HelloAvalonia{n}";
            var path = Path.Combine(folder, candidate);
            if (!Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any())
            {
                return candidate;
            }
        }
    }

    private async void AddWindow_Click(object sender, RoutedEventArgs e) => await AddItemAsync(ItemKind.Window);

    private async void AddUserControl_Click(object sender, RoutedEventArgs e) => await AddItemAsync(ItemKind.UserControl);

    /// <summary>Project ▸ Add: a class name; the files go next to the open document (or in the project folder).</summary>
    private async Task AddItemAsync(ItemKind kind)
    {
        if (ViewModel.Project is not { } project)
        {
            return;
        }

        var folder = ViewModel.DefaultItemFolder;
        var label = kind == ItemKind.Window ? "window" : "user control";
        var name = new TextBox { Header = "Class name", Text = kind == ItemKind.Window ? "SecondWindow" : "MyControl", MinWidth = 360 };
        var where = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["NotifyErrorIcon"] };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Add {label}",
            Content = new StackPanel { Spacing = 12, Children = { name, where, problem } },
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        void Validate()
        {
            var error = ProjectTemplates.ValidateItemName(folder, name.Text.Trim());
            where.Text = error is null
                ? $"Creates {Path.GetRelativePath(project.Directory, Path.Combine(folder, name.Text.Trim() + ".axaml"))} and its .axaml.cs in {project.Name}."
                : "";
            problem.Text = error ?? "";
            dialog.IsPrimaryButtonEnabled = error is null;
        }

        name.TextChanged += (_, _) => Validate();
        Validate();
        if (await dialog.ShowOnceAsync() == ContentDialogResult.Primary
            && await ViewModel.AddItemAsync(kind, name.Text.Trim(), folder) is { } failed)
        {
            ShowNotification(NotifyKind.Error, $"Couldn't add the {label}: {failed}");
        }
    }
}
