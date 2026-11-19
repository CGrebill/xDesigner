using XDesigner.App.Services;
using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using XDesigner.Core.Settings;

namespace XDesigner.App;

/// <summary>The Help menu: About, license, third-party notices, issue reporting, the app's data folder.</summary>
public sealed partial class MainWindow
{
    private static readonly Assembly AppAssembly = typeof(MainWindow).Assembly;

    /// <summary>The version shown to people (1.0.0), without the build metadata the SDK appends.</summary>
    private static string ProductVersion =>
        (AppAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")
            .Split('+')[0] is { Length: > 0 } version ? version : AppAssembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>Where the source lives (RepositoryUrl in Directory.Build.props).</summary>
    private static string? RepositoryUrl =>
        AppAssembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value;

    private static string? Copyright => AppAssembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;

    private void ReportProblem_Click(object sender, RoutedEventArgs e) =>
        OpenExternal(RepositoryUrl is { } url ? $"{url}/issues" : null, "the issue tracker");

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppDataPaths.Folder);
        OpenExternal(AppDataPaths.Folder, "the settings folder");
    }

    private void License_Click(object sender, RoutedEventArgs e) =>
        OpenExternal(Path.Combine(AppContext.BaseDirectory, "LICENSE.txt"), "the license");

    private void ThirdPartyNotices_Click(object sender, RoutedEventArgs e) =>
        OpenExternal(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt"), "the third-party notices");

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var links = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (RepositoryUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            links.Inlines.Add(new Hyperlink { NavigateUri = uri, Inlines = { new Run { Text = url } } });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "xDesigner",
            CloseButtonText = "Close",
            Content = new StackPanel
            {
                Spacing = 10,
                MaxWidth = 440,
                Children =
                {
                    new TextBlock { Text = $"Version {ProductVersion}", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                    new TextBlock { Text = "A visual designer for Avalonia UI: drag controls onto a form, set their properties, and write only the C# behind them.", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = Copyright ?? "", Foreground = secondary },
                    new TextBlock
                    {
                        Text = "Licensed under the PolyForm Noncommercial License 1.0.0 (Help › License). Not affiliated with or endorsed by the Avalonia project or AvaloniaUI OÜ.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = secondary,
                    },
                    links,
                },
            },
        };
        await dialog.ShowOnceAsync();
    }

    /// <summary>Opens a URL, folder or file with its default program; says so in a notification if that fails.</summary>
    private void OpenExternal(string? target, string what)
    {
        if (target is null)
        {
            ShowNotification(NotifyKind.Warning, $"Couldn't open {what}: it isn't configured in this build.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            ShowNotification(NotifyKind.Warning, $"Couldn't open {what} ({target}): {ex.Message}");
        }
    }
}
