# Changelog

All notable changes to xDesigner are listed here. Versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed
- **WinUI:** on a slow PC (or one without a graphics card), images could be drawn blank, because the design was captured
  before WinUI had painted them. It's now drawn until the picture settles (up to 1.5 s, only when the form has images).
- **WinUI:** an image of the project's that took more than 1.5 s to load stayed blank in the designer until the next build.
  It's now waited for again on the next render; only a web image that times out is given up on. Images that can't be
  loaded, or that take too long, are now reported in the renderer's output.

## [2.0.0] - 2026-11-25

### Added
- **Avalonia 11.** Projects on Avalonia 11.1 or later can be designed, drawn by Avalonia 11 itself (a second renderer,
  built from the same source against Avalonia 11.3). New project offers Avalonia 12 or 11.
- **Whole solutions.** Opening a `.sln`/`.slnx` opens all its projects, each with its own framework version, so an
  Avalonia 11 and an Avalonia 12 project can be designed side by side. Build builds the solution; Run starts the
  startup project.
- **Solution Explorer** (beside the Document Outline): projects with their framework, folders, XAML and C# files.
  Double-click to design a XAML file or open any other file in your code editor; right-click to set the startup project
  or show a file in File Explorer.
- **Error List** in Output: the last build's errors and warnings; double-click to go to one.
- **View › Toolbox / Output** hide or show those panes; **View › Solution Explorer** (Ctrl+Alt+L) and **Error List**.
- **WinUI 3.** WinUI 3 projects (Windows App SDK, unpackaged or packaged) can be designed: windows, pages and user
  controls are drawn by WinUI itself, with the project's App.xaml resources, images and custom controls. The toolbox
  lists WinUI's controls; grid rows and columns are written as `<Grid.RowDefinitions>`; TabView, ComboBox and ListView
  items have the collection editor; images are added as `ms-appx:///Assets/…` content. New project creates a WinUI 3 app
  (unpackaged or packaged), Project › Add page… a page. Builds pass the platform WinUI needs, and Run starts a packaged
  app as a registered package (with Developer Mode on) or its exe directly.
- **.NET MAUI 10 and 11.** MAUI projects can be designed: content pages and content views are drawn by MAUI itself (the
  project's own MauiProgram, App styles, fonts, images and controls, through MAUI's Windows handlers). Placement writes
  MAUI's attributes (WidthRequest, HorizontalOptions, AbsoluteLayout.LayoutBounds); MAUI 11 files without namespace
  declarations open, edit and save as written; images are added as MauiImages. Builds and Run target Windows only (no
  Android or iOS workloads needed). New project creates a MAUI 10 or 11 app (unpackaged or packaged).
- The installer lets you choose which renderers to install (Avalonia 12, Avalonia 11, WinUI 3, .NET MAUI 11 and 10).

### Changed
- Render hosts live in `renderhosts\{framework}` next to the app (one per framework version) and share their
  framework-free code (`XDesigner.RenderHost.Common`); the render protocol is version 6.
- xDesigner is built with the .NET 11 SDK (its MAUI 11 renderer needs it); building it also needs the MAUI workload.
- New projects go to `Documents\xDesigner Projects` by default, with a name suggested for the chosen framework.
- A solution remembers its startup project. A build can be cancelled (the Rebuild item reads Cancel build while one
  runs, and closing the designer cancels it). Run, Rebuild and Add are disabled while they can't act.
- Renderers no open file uses are stopped (after closing their files or opening another solution).
- The MAUI renderers are about 55 MB smaller each (the Windows App SDK's AI and machine-learning parts are left out).

### Fixed (from a full code review of 2.0)
- Saving could write a malformed file: a declaration the designer added (`xmlns:x`, `xmlns:d`) could reuse a prefix the
  file already used, and an end tag could get a different prefix from its start tag when a namespace had two.
- A name typed in the Name box and refused could rename a control in the next document.
- MAUI pages using `OnPlatform`, `OnIdiom`, `StaticResource` or `DynamicResource` elements didn't render; nor did files
  in MAUI's global namespace.
- Render errors pointed at the wrong line once a start tag spanned several lines.
- Moving a MAUI control with proportional `AbsoluteLayout.LayoutFlags` threw it off the layout, and moving one out of an
  AbsoluteLayout lost its size; pasting another framework's controls inserted XAML that couldn't render; WinUI tabs in
  `<TabView.TabItems>` were missing from the outline; controls dropped into `<StackPanel.Children>` landed after it;
  MAUI corner radii were written in the wrong order; Events listed no handlers to reuse in MAUI files.
- When one project of a solution failed to build, no project's renderer got its new build; rebuilding a referenced
  library didn't reload the app's controls; builds outside the designer were noticed for the startup project only.
- Projects whose framework version is set in `Directory.Build.props` or central package management weren't recognised
  before their first build; WPF windows were taken for WinUI ones; a MAUI project that also targets plain .NET was built
  for it; a multi-targeted app couldn't be run.
- The WinUI renderer stretched roots with MaxWidth or Margin, ignored App.xaml's RequestedTheme and some App.xaml
  resource forms, showed `x:Uid` strings and scale-qualified images empty, dropped `<Window.Content>`, and ran the
  bootstrapper of framework-dependent projects. Controls from libraries nothing had loaded yet could be missing.
- The MAUI renderer kept the project's main page running unseen, could let a window the project opened later appear,
  reported style values as set locally, and counted hidden controls in Auto rows.
- A project App that hung while starting never got the default-theme fallback. Avalonia Grids with RowSpacing reported
  a last row edge past the grid. A broken image slowed every render by 1.5 s.
- Opening a solution during a build, or one with no readable project, left things half switched; Project ▸ Add could
  create Avalonia files in a test or library project; an open file deleted on disk was recreated by Save; two designers
  open lost each other's recent files; the same file opened twice quickly got two tabs.
- New project accepted relative or invalid folders; Show build output did nothing with the Output pane hidden; Ctrl+X in
  Solution Explorer cut controls from the form; Solution Explorer's menu didn't open from the keyboard.

### Fixed (from a full code review of 1.0.0)
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

[Unreleased]: https://github.com/CGrebill/xDesigner/compare/v2.0.0...HEAD
[2.0.0]: https://github.com/CGrebill/xDesigner/releases/tag/v2.0.0
[1.0.0]: https://github.com/CGrebill/xDesigner/releases/tag/v1.0.0
