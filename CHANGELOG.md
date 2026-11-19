# Changelog

All notable changes to xDesigner are listed here. Versions follow [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-11-19

First public release.

- Visual designer for Avalonia 12 windows and user controls, rendered by Avalonia itself in a separate process.
- Toolbox with the built-in controls and the project's custom controls; panel-aware drag and drop (Grid, Canvas,
  StackPanel, WrapPanel, DockPanel, content controls); move, resize, snap lines, grid snapping, multi-select, rubber
  band, nudge, copy and paste.
- Properties window with typed editors and an Events tab; double-click creates event handlers in the code-behind and
  reports where.
- Document outline with drag and drop, context menu (Wrap in, z-order, item and grid editors) and design-time hiding.
- Collection editor (ListBox, ComboBox, TabControl, Menu, TreeView) and Grid rows/columns editor.
- Preview theme, preview size and zoom; image picker that copies images into the project.
- Projects: open `.csproj`/`.sln`, build, run and stop; new project wizard; new windows and user controls; reloads
  files and assemblies changed outside the designer.
- Lossless XAML editing with exact undo.
- Per-user installer and an uninstaller that removes all of xDesigner's files and settings.

[1.0.0]: https://github.com/CGrebill/xDesigner/releases/tag/v1.0.0
