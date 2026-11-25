using System.ComponentModel;
using System.Diagnostics;
using XDesigner.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace XDesigner.App.Controls;

/// <summary>
/// Solution Explorer: every project of the open solution, each labelled with its framework, and their files. Double-click
/// opens a XAML file in the designer and any other file in the code editor; the right-click menu sets the startup project
/// and reveals files.
/// </summary>
public sealed partial class SolutionExplorer : UserControl
{
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private ShellViewModel? _shell;

    public SolutionExplorer()
    {
        InitializeComponent();
    }

    /// <summary>Raised to show a message in the window's notification strip.</summary>
    public event Action<string>? Message;

    public void Attach(ShellViewModel shell)
    {
        _shell = shell;
        shell.PropertyChanged += OnShellPropertyChanged;
        Rebuild();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.Solution) or nameof(ShellViewModel.Project))
        {
            Rebuild();
        }
    }

    /// <summary>Rebuilds the tree (after opening, a build, or a new file), keeping open folders open.</summary>
    public void Rebuild()
    {
        var solution = _shell?.Solution;
        Tree.ItemsSource = solution is null ? null : SolutionNodeViewModel.Build(solution, _shell?.Project?.ProjectPath, _expanded, _collapsed);
        EmptyText.Visibility = solution is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Tree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is SolutionNodeViewModel node)
        {
            _expanded.Add(node.Path);
            _collapsed.Remove(node.Path);
        }
    }

    private void Tree_Collapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        if (args.Item is SolutionNodeViewModel node)
        {
            _expanded.Remove(node.Path);
            _collapsed.Add(node.Path);
        }
    }

    // Enter on a file opens it, like a double-click.
    private void Tree_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && Tree.SelectedItem is SolutionNodeViewModel node)
        {
            e.Handled = true;
            Open(node);
        }
    }

    private void Item_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SolutionNodeViewModel node })
        {
            e.Handled = true;
            Open(node);
        }
    }

    private void Open(SolutionNodeViewModel node)
    {
        switch (node.Kind)
        {
            case SolutionNodeKind.XamlFile:
                _ = _shell?.OpenFileAsync(node.Path);
                break;
            case SolutionNodeKind.CodeFile:
                OpenInCodeEditor(node);
                break;
        }
    }

    private void OpenInCodeEditor(SolutionNodeViewModel node)
    {
        if (_shell?.CodeEditor.Open(node.Path, 1) is { } problem)
        {
            Message?.Invoke(problem);
        }
    }

    /// <summary>Moves keyboard focus into the tree (View ▸ Solution Explorer, review L51).</summary>
    public void FocusTree() => Tree.Focus(FocusState.Programmatic);

    // ContextRequested, not RightTapped: also Shift+F10 and the Menu key (review L51).
    private void Tree_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var target = e.OriginalSource as FrameworkElement;
        if ((target?.DataContext as SolutionNodeViewModel ?? Tree.SelectedItem as SolutionNodeViewModel) is not { } node || _shell is not { } shell)
        {
            return;
        }

        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        switch (node.Kind)
        {
            case SolutionNodeKind.Project when node.Project is { } project:
                Add("Set as startup project", "", () => shell.SetStartupProject(project), enabled: !node.IsStartup && project.IsDesignable); // Add and Run act on it (review M5)
                Add("Open project file in code editor", "", () => OpenInCodeEditor(node));
                Add("Show in File Explorer", "", () => Reveal(project.Info.Directory, select: false));
                if (project.Framework.Problem(project.Info.Name) is { } problem)
                {
                    menu.Items.Add(new MenuFlyoutSeparator());
                    menu.Items.Add(new MenuFlyoutItem { Text = problem, IsEnabled = false });
                }

                break;
            case SolutionNodeKind.XamlFile:
                Add("Open in designer", "", () => Open(node));
                Add("Open in code editor", "", () => OpenInCodeEditor(node));
                Add("Show in File Explorer", "", () => Reveal(node.Path, select: true));
                break;
            case SolutionNodeKind.CodeFile:
                Add("Open in code editor", "", () => OpenInCodeEditor(node));
                Add("Show in File Explorer", "", () => Reveal(node.Path, select: true));
                break;
            default:
                Add("Show in File Explorer", "", () => Reveal(node.Path, select: node.Kind == SolutionNodeKind.Solution));
                break;
        }

        var anchor = target ?? Tree;
        if (e.TryGetPosition(anchor, out var point))
        {
            menu.ShowAt(anchor, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
        }
        else
        {
            menu.ShowAt(anchor); // from the keyboard: at the row
        }

        e.Handled = true;
    }

    /// <summary>Opens File Explorer at a folder, or at a file's folder with the file selected.</summary>
    private void Reveal(string path, bool select)
    {
        try
        {
            // explorer.exe parses "/select," itself, so the argument is passed as one string (paths can't contain quotes).
            Process.Start(new ProcessStartInfo("explorer.exe", select ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Message?.Invoke($"Couldn't open File Explorer: {ex.Message}");
        }
    }
}
