# Changelog

All notable changes to xDesigner are listed here. Versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

Fixes from a full code review of 1.0.0.

### Fixed
- A property value still being typed was applied to the wrong file when another file was opened or dropped on the
  window (and a pending handler name could create the method in the other file's code-behind).
- Adding an image could delete content from the `.csproj` when other text shared the `</Project>` line (or the whole
  file was on one line). The designer now checks the result loads before writing it, and leaves non-UTF-8 project
  files alone.
- Saving rewrote element text nobody edited: entities such as `&quot;` and `&#160;`, an unescaped `>`, line endings
  in files with mixed line endings, and an encoded line break inside text (which became two). Unedited text,
  comments and CDATA are now written back exactly as they were.
- Saving (Ctrl+S, or before a build) no longer overwrites a file changed outside the designer while the question
  about it is pending; the question is asked again.
- Pressing Run or Restart twice could start two builds and leave a second copy of the app running after the designer
  closed.
- Reloading a file changed on disk could replace the wrong tab if an earlier tab was closed meanwhile.
- "Open in editor" with VS Code broke on paths containing `&` or similar characters; a custom editor command written
  as `"{file}:{line}"` broke on paths with spaces.
- Build errors were missed (and the result read "0 errors") for paths with parentheses, errors without a position
  such as `NU1101`, and errors with a subcategory such as `Avalonia error AVLN2000`.
- A handler already defined in another file of the same partial class was added again (CS0111); a method name equal to
  a control's name clashed with its field. Code-behind files that aren't UTF-8, or that have syntax errors, are now
  refused instead of being changed.
- Project names such as `Window` or `Avalonia`, and items named `Window` or `UserControl`, are refused: they created
  projects that didn't compile. A project title starting with `{` no longer breaks the new project.
- The Grid rows and columns editor no longer drops settings it doesn't edit (SharedSizeGroup, names, resources,
  comments); such grids are reported as not editable.
- Pasting a control whose attribute values use a prefix (`x:DataType="vm:Item"`) declares that prefix; pasting plain
  XAML that uses `x:Name` works.
- Moving a control within its own DockPanel now changes its order as well as its edge; resizing one edge of a
  stretched control in a Grid keeps its other dimension; moving or nudging a control on a Canvas no longer drifts by
  its Margin; nudging several selected controls in a StackPanel moves them as a block.
- Tab no longer crashes inside a TabItem header; a document stays marked as changed after more than 500 edits are
  undone; `NaN` margins, out-of-range whole numbers and negative grid lengths are rejected.
- Renderer: an exception from a user control's timer or event handler no longer stops the renderer; very large
  previews are drawn at a lower resolution instead of failing; error line numbers now count lines before the root
  element; a renderer that failed to start no longer leaves a thread running; the renderer exits when the designer
  closes even while the project's App is still starting; its diagnostics are logged when the fallback start fails
  too; the designer only connects to a renderer running as the same user.
- Opening a second dialog while one is showing (pressing the close button twice) no longer shows "Something went
  wrong". Run and Rebuild include a property value still being typed. Settings files containing `null` values no
  longer crash startup.

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
