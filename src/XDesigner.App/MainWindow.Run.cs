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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "xDesigner Projects"); // for every framework

    private string? _lastProjectsFolder;

    private void UpdateRunState()
    {
        var project = ViewModel.Project;
        var running = ViewModel.IsRunning;
        RunButton.IsEnabled = RunMenuItem.IsEnabled = project is not null && !ViewModel.IsBusy; // busy: a press would be ignored (review L22)
        RunButtonText.Text = running ? "Restart" : "Run";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RunButton, RunButtonText.Text); // read as shown (review L52)
        ToolTipService.SetToolTip(RunButton, running ? "Stop, rebuild and run again (F5)" : "Save, build and run the project (F5)");
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StopMenuItem.IsEnabled = running;
        AddWindowMenuItem.IsEnabled = AddUserControlMenuItem.IsEnabled = AddPageMenuItem.IsEnabled = project is not null && ViewModel.ProjectProfile is not null; // review M5
        var kinds = project is not null && ViewModel.ProjectProfile is { } profile ? ProjectTemplates.ItemKindsFor(profile) : [ItemKind.Window, ItemKind.UserControl];
        AddPageMenuItem.Visibility = kinds.Contains(ItemKind.Page) ? Visibility.Visible : Visibility.Collapsed;
        AddWindowMenuItem.Visibility = kinds.Contains(ItemKind.Window) ? Visibility.Visible : Visibility.Collapsed;
        var maui = ViewModel.ProjectProfile?.Family == Core.Frameworks.FrameworkFamily.Maui;
        AddPageMenuItem.Text = maui ? "Add content page…" : "Add page…";
        AddUserControlMenuItem.Text = maui ? "Add content view…" : "Add user control…";
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
        var frameworksList = ProjectTemplates.CreatableFrameworks;
        var suggestedName = NextFreeName(_lastProjectsFolder ?? DefaultProjectsFolder, frameworksList[0]);
        var name = new TextBox { Header = "Project name", PlaceholderText = suggestedName, Text = suggestedName };
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
        var packaged = new ToggleSwitch
        {
            Header = "Packaged (MSIX)",
            OffContent = "Unpackaged: runs as an ordinary exe",
            OnContent = "Packaged: runs as an installed app with package identity",
        };
        void Describe()
        {
            var selected = frameworks[Math.Max(0, framework.SelectedIndex)];
            var canPackage = selected.Family is Core.Frameworks.FrameworkFamily.WinUI or Core.Frameworks.FrameworkFamily.Maui;
            packaged.Visibility = canPackage ? Visibility.Visible : Visibility.Collapsed;
            var packagedNote = canPackage && packaged.IsOn ? " Running a packaged app from xDesigner registers it with Windows, which needs Developer Mode." : "";

            // The suggestions follow the framework until the name is typed over.
            title.Header = selected.Family == Core.Frameworks.FrameworkFamily.Maui ? "Main page title" : "Window title";
            if (name.Text == suggestedName)
            {
                suggestedName = NextFreeName(location.Text.Trim(), selected);
                name.Text = name.PlaceholderText = suggestedName;
            }
            description.Text = selected.Family switch
            {
                Core.Frameworks.FrameworkFamily.WinUI =>
                    $"A Windows desktop app ({selected.DisplayName}, Windows App SDK {selected.TemplateVersion}, .NET 10) with an empty main window.{packagedNote}",
                Core.Frameworks.FrameworkFamily.Maui =>
                    $"A .NET MAUI app ({selected.DisplayName}, .NET {selected.MajorVersion}) with an empty main page, built for Windows (add Android, iOS or macOS later).{packagedNote}",
                _ => $"A desktop app for Windows, macOS and Linux ({selected.DisplayName}, .NET 10) with an empty main window.",
            };
        }

        framework.SelectionChanged += (_, _) => Describe();
        packaged.Toggled += (_, _) => Describe();
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
                Children = { framework, description, packaged, name, locationRow, title, where, problem },
            },
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        void Validate()
        {
            var error = ProjectTemplates.ValidateProjectName(name.Text.Trim(), frameworks[Math.Max(0, framework.SelectedIndex)]);
            var folder = location.Text.Trim();
            var target = "";
            if (error is null)
            {
                // A full path only: a relative one would land in whatever the current folder is (review M33).
                error = folder.Length == 0 ? "Choose a folder for the project."
                    : folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || folder.Contains('|') ? "The folder contains characters a path can't have."
                    : !Path.IsPathFullyQualified(folder) ? @"Enter the folder's full path, such as C:\Users\you\Documents\Projects."
                    : !Directory.Exists(Path.GetPathRoot(folder)) ? $"There is no drive {Path.GetPathRoot(folder)}."
                    : null;
            }

            if (error is null)
            {
                try
                {
                    target = Path.GetFullPath(Path.Combine(folder, name.Text.Trim()));
                    if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                    {
                        error = $"{target} already exists and isn't empty.";
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    error = $"That folder can't be used: {ex.Message}";
                }
            }

            where.Text = error is null ? $"Creates {Path.Combine(target, name.Text.Trim() + ".csproj")}" : "";
            problem.Text = error ?? "";
            dialog.IsPrimaryButtonEnabled = error is null;
        }

        name.TextChanged += (_, _) => Validate();
        framework.SelectionChanged += (_, _) => Validate(); // each framework reserves other names
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

        var parent = Path.GetFullPath(location.Text.Trim());
        try
        {
            Directory.CreateDirectory(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowNotification(NotifyKind.Error, $"Couldn't create the folder {parent}: {ex.Message}");
            return;
        }

        _lastProjectsFolder = parent;
        var chosen = frameworks[Math.Max(0, framework.SelectedIndex)];
        var asPackage = chosen.Family is Core.Frameworks.FrameworkFamily.WinUI or Core.Frameworks.FrameworkFamily.Maui && packaged.IsOn;
        if (await ViewModel.CreateProjectAsync(_lastProjectsFolder, name.Text.Trim(), title.Text, chosen, asPackage) is { } failed)
        {
            ShowNotification(NotifyKind.Error, $"Couldn't create the project: {failed}");
        }
    }

    /// <summary><c>HelloAvalonia</c>, <c>HelloWinUI</c> or <c>HelloMaui</c>, numbered when that folder is taken.</summary>
    private static string NextFreeName(string folder, Core.Frameworks.FrameworkProfile framework)
    {
        var stem = "Hello" + framework.Family switch
        {
            Core.Frameworks.FrameworkFamily.WinUI => "WinUI",
            Core.Frameworks.FrameworkFamily.Maui => "Maui",
            _ => "Avalonia",
        };
        for (var n = 1; n < 1000; n++)
        {
            var candidate = n == 1 ? stem : $"{stem}{n}";
            try
            {
                var path = Path.Combine(folder, candidate);
                if (!Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any())
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return candidate; // the location is checked when it's validated
            }
        }

        return stem;
    }

    private async void AddWindow_Click(object sender, RoutedEventArgs e) => await AddItemAsync(ItemKind.Window);

    private async void AddUserControl_Click(object sender, RoutedEventArgs e) => await AddItemAsync(ItemKind.UserControl);

    private async void AddPage_Click(object sender, RoutedEventArgs e) => await AddItemAsync(ItemKind.Page);

    /// <summary>Project ▸ Add: a class name; the files go next to the open document (or in the project folder).</summary>
    private async Task AddItemAsync(ItemKind kind)
    {
        if (ViewModel.Project is not { } project)
        {
            return;
        }

        var profile = ViewModel.ProjectProfile ?? Core.Frameworks.FrameworkProfile.Avalonia12;
        var extension = profile.FileExtension;
        var folder = ViewModel.DefaultItemFolder;
        var maui = profile.Family == Core.Frameworks.FrameworkFamily.Maui;
        var (label, suggested) = kind switch
        {
            ItemKind.Window => ("window", "SecondWindow"),
            ItemKind.Page => (maui ? "content page" : "page", "SecondPage"),
            _ => (maui ? "content view" : "user control", maui ? "MyView" : "MyControl"),
        };
        var name = new TextBox { Header = "Class name", Text = suggested, MinWidth = 360 };
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
            var error = ProjectTemplates.ValidateItemName(folder, name.Text.Trim(), profile);
            where.Text = error is null
                ? $"Creates {Path.GetRelativePath(project.Directory, Path.Combine(folder, name.Text.Trim() + extension))} and its {extension}.cs in {project.Name}."
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
