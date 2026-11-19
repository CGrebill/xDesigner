using XDesigner.Core.Settings;

namespace XDesigner.App.ViewModels;

/// <summary>Settings (§15.3) and the recent files list.</summary>
public sealed partial class ShellViewModel
{
    private readonly SettingsStore _settingsStore = new(SettingsStore.DefaultPath);

    /// <summary>The designer's settings; call <see cref="SaveSettings"/> after changing them.</summary>
    public DesignerSettings Settings { get; private set; } = new();

    /// <summary>Where code is opened for editing (the external editor from Settings, for now).</summary>
    public Services.ICodeEditorService CodeEditor => _codeEditor ??= new Services.ExternalCodeEditor(() => Settings);

    private Services.ICodeEditorService? _codeEditor;

    /// <summary>Raised after <see cref="DesignerSettings.Recent"/> changed (menus and the start page rebuild).</summary>
    public event EventHandler? RecentChanged;

    /// <summary>Raised after the settings were changed in the settings dialog.</summary>
    public event EventHandler? SettingsChanged;

    public string SettingsPath => _settingsStore.Path;

    public void LoadSettings() => Settings = _settingsStore.Load();

    public void SaveSettings()
    {
        if (!_settingsStore.Save(Settings))
        {
            AppendLog($"Couldn't save the settings to {_settingsStore.Path}.");
        }
    }

    /// <summary>The settings dialog applied changes: save them and let the window and documents pick them up.</summary>
    public void ApplySettings()
    {
        SaveSettings();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Opens a recent entry; one that no longer exists is taken off the list.</summary>
    public async Task OpenRecentAsync(string path)
    {
        if (!File.Exists(path))
        {
            Settings.RemoveRecent(path);
            SaveSettings();
            RecentChanged?.Invoke(this, EventArgs.Empty);
            RaiseNotify(NotifyKind.Warning, $"{Path.GetFileName(path)} isn't there any more, so it was taken off the recent list.");
            return;
        }

        await OpenAnyAsync(path);
    }

    private void Remember(string path)
    {
        Settings.AddRecent(path);
        SaveSettings();
        RecentChanged?.Invoke(this, EventArgs.Empty);
    }
}
