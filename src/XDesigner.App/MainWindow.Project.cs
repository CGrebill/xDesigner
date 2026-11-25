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
        ViewModel.ShowBuildOutput += () => ShowOutputTab(BuildTab);
        ViewModel.ShowRunOutput += () => ShowOutputTab(RunTab);
        ViewModel.RunLines.CollectionChanged += (_, _) =>
        {
            if (ViewModel.RunLines.Count > 0)
            {
                RunList.ScrollIntoView(ViewModel.RunLines[^1]);
            }
        };
        ViewModel.ProjectFilesChanged += UpdateProjectFilesMenu;
        ViewModel.BuildLines.CollectionChanged += OnBuildLinesChanged;
        ViewModel.PropertyChanged += OnProjectPropertyChanged;
        ViewModel.Diagnostics.CollectionChanged += (_, _) =>
        {
            NoErrorsText.Visibility = ViewModel.Diagnostics.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ErrorsTab.Text = ViewModel.Diagnostics.Count == 0 ? "Error List" : $"Error List ({ViewModel.Diagnostics.Count})";
        };
        SolutionPane.Attach(ViewModel);
        SolutionPane.Message += message => ShowNotification(NotifyKind.Warning, message);
        UpdateProjectState();
    }

    private void OnProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.Project) or nameof(ShellViewModel.IsBuilding) or nameof(ShellViewModel.IsRunning)
            or nameof(ShellViewModel.IsBusy) or nameof(ShellViewModel.Solution))
        {
            UpdateProjectState();
        }

        // A solution with several projects opens on Solution Explorer: that's where they are.
        if (e.PropertyName is nameof(ShellViewModel.Solution) && ViewModel.Solution is { Projects.Count: > 1 } && !_solutionShown)
        {
            _solutionShown = true;
            SideTabs.SelectedItem = SolutionTab;
        }
        else if (e.PropertyName is nameof(ShellViewModel.Solution) && ViewModel.Solution is null)
        {
            _solutionShown = false;
        }
    }

    private bool _solutionShown;

    private void UpdateProjectState()
    {
        var project = ViewModel.Project;
        var solution = ViewModel.Solution;
        // While a build runs the item cancels it (review L23); while something else is busy it waits.
        RebuildMenuItem.IsEnabled = project is not null && (ViewModel.IsBuilding || !ViewModel.IsBusy);
        RebuildMenuItem.Text = ViewModel.IsBuilding ? "Cancel build"
            : solution is { SolutionPath: not null } ? $"Rebuild solution {solution.Name}"
            : project is null ? "Rebuild" : $"Rebuild {project.Name}";
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
        if (ViewModel.IsBuilding)
        {
            ViewModel.CancelBuild();
            return;
        }

        Properties.FlushPendingEdits(); // the build saves the documents: include a value still being typed (§9.2)
        await ViewModel.BuildAsync();
    }

    private void CloseProject_Click(object sender, RoutedEventArgs e) => ViewModel.CloseProject();
}
