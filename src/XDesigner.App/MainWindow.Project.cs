using XDesigner.App.Services;
using System.Collections.Specialized;
using System.ComponentModel;
using XDesigner.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace XDesigner.App;

/// <summary>Project menu, Build output and project notifications (§11).</summary>
public sealed partial class MainWindow
{
    private void InitializeProject()
    {
        ViewModel.Notify += r => ShowNotification(r.Kind, r.Message, r.Actions, r.Sticky);
        ViewModel.ShowBuildOutput += () => OutputTabs.SelectedItem = BuildTab;
        ViewModel.ShowRunOutput += () => OutputTabs.SelectedItem = RunTab;
        ViewModel.RunLines.CollectionChanged += (_, _) =>
        {
            if (ViewModel.RunLines.Count > 0)
            {
                RunList.ScrollIntoView(ViewModel.RunLines[^1]);
            }
        };
        ViewModel.ProjectFilesChanged += UpdateProjectFilesMenu;
        ViewModel.ChooseProject = ChooseProjectAsync;
        ViewModel.BuildLines.CollectionChanged += OnBuildLinesChanged;
        ViewModel.PropertyChanged += OnProjectPropertyChanged;
        UpdateProjectState();
    }

    private void OnProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.Project) or nameof(ShellViewModel.IsBuilding) or nameof(ShellViewModel.IsRunning))
        {
            UpdateProjectState();
        }
    }

    private void UpdateProjectState()
    {
        var project = ViewModel.Project;
        RebuildMenuItem.IsEnabled = project is not null && !ViewModel.IsBuilding;
        RebuildMenuItem.Text = ViewModel.IsBuilding ? "Building…" : project is null ? "Rebuild" : $"Rebuild {project.Name}";
        CloseProjectMenuItem.IsEnabled = project is not null;
        UpdateRunState();
        SyncFromSelectedDocument(); // title shows the project
    }

    private void OnBuildLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset || ViewModel.BuildLines.Count == 1)
        {
            OutputTabs.SelectedItem = BuildTab; // a build started
        }

        if (ViewModel.BuildLines.Count > 0)
        {
            BuildList.ScrollIntoView(ViewModel.BuildLines[^1]);
        }
    }

    /// <summary>Project ▸ Files: the project's .axaml files, by path relative to the project.</summary>
    private void UpdateProjectFilesMenu()
    {
        ProjectFilesMenu.Items.Clear();
        if (ViewModel.Project is not { } project)
        {
            ProjectFilesMenu.IsEnabled = false;
            return;
        }

        foreach (var file in ViewModel.ProjectFiles)
        {
            var item = new MenuFlyoutItem { Text = Path.GetRelativePath(project.Directory, file) };
            item.Click += async (_, _) => await ViewModel.OpenFileAsync(file);
            ProjectFilesMenu.Items.Add(item);
        }

        ProjectFilesMenu.IsEnabled = ProjectFilesMenu.Items.Count > 0;
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var type in new[] { ".csproj", ".sln", ".slnx" })
        {
            picker.FileTypeFilter.Add(type);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (await picker.PickSingleFileAsync() is { } file)
        {
            await ViewModel.OpenAnyAsync(file.Path);
        }
    }

    private async void Rebuild_Click(object sender, RoutedEventArgs e)
    {
        Properties.FlushPendingEdits(); // the build saves the documents: include a value still being typed (§9.2)
        await ViewModel.BuildAsync();
    }

    private void CloseProject_Click(object sender, RoutedEventArgs e) => ViewModel.CloseProject();

    /// <summary>A solution with several XAML projects: which one to design.</summary>
    private async Task<string?> ChooseProjectAsync(IReadOnlyList<string> projects)
    {
        var list = new ListView
        {
            ItemsSource = projects.Select(Path.GetFileNameWithoutExtension).ToList(),
            SelectionMode = ListViewSelectionMode.Single,
            SelectedIndex = 0,
            MaxHeight = 320,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Which project?",
            Content = new StackPanel
            {
                Spacing = 8,
                Children = { new TextBlock { Text = "This solution has several projects with .axaml files.", TextWrapping = TextWrapping.Wrap }, list },
            },
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowOnceAsync() == ContentDialogResult.Primary && list.SelectedIndex >= 0 ? projects[list.SelectedIndex] : null;
    }
}
