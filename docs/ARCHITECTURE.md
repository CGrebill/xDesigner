# xDesigner architecture

How xDesigner works, for contributors. Code comments refer to the sections here as `§n.n`.

## 1. Purpose

xDesigner is a WinForms-style visual designer for C# UI frameworks: today [Avalonia UI](https://avaloniaui.net/) 11 and
12, WinUI 3 (Windows App SDK, packaged or unpackaged) and .NET MAUI 10 and 11 (as they look on Windows). You drag controls from a toolbox onto a form, edit them in a
properties window, double-click to create event handlers, and write only C#: the designer never shows XAML, it writes
it. It opens whole solutions (projects of different frameworks side by side, each drawn by its own framework), designs
their Windows and UserControls, creates projects, and builds and runs them.

### 1.0 Frameworks
Everything framework-specific is described by a `FrameworkProfile` (`Core/Frameworks`): XML namespaces, file
extension, the CLR names of the built-in controls the designer special-cases, how custom-control namespaces are written,
value conventions, the toolbox list, the project template and which render host draws it. Every `XamlDocument` carries
one (`Profile`, from its root's namespace, made exact by its project), and every `ControlCatalog` carries the one of the
host that reported it. `FrameworkDetector` finds a project's framework and version from its build output (`deps.json`,
`Avalonia.Base.dll`) or, before a build, from its project file. Each framework major version has its own render host
(§14), since one process can't load two versions of a framework.

### 1.1 Goals
- Real Avalonia rendering of the form being designed, including the project's theme, fonts and custom controls.
- Typed editing of everything shown: no raw XAML entry.
- Lossless editing: a file changes only where the user edited it; undo restores it byte for byte.
- Familiar designer ergonomics: toolbox, properties, events, outline, undo/redo, clipboard, multi-select, snapping.

### 1.2 Non-goals
Authoring data bindings, styles, resources, templates or animations (existing ones are kept and shown read-only);
showing or editing XAML text (for now: a code editor is on the roadmap, behind `ICodeEditorService`); mobile/browser
targets; Avalonia versions before 11.1; running the designer itself on anything but Windows.

### 1.3 Design decisions
| Decision | Why |
|---|---|
| Avalonia renders in a separate, headless process; the WinUI app shows its pixels and draws overlays | WinUI can't host Avalonia visuals; user code (custom controls) can crash or hang and must never take the designer down; a fresh process per build reloads assemblies cleanly |
| The XAML text is the source of truth, held by the app | The renderer stays stateless (it gets the whole document each time), so undo, saving and naming live in one place |
| Edits are commands on an `XDocument` with custom, lossless serialization | Users' files keep their formatting, comments and attribute order |
| Code-behind is edited with Roslyn syntax trees, as text | Handlers are inserted precisely without reformatting the user's code |
| Placement is panel-aware (Grid cells, Canvas positions, Stack order, Dock edges) | Forms are built from Avalonia panels, not absolute positions |
| Handlers are named `{control}_{Event}`; renaming or deleting a control never touches C# | Like WinForms: code the user wrote is theirs |
| Saving is manual (Ctrl+S) and prompts on close; code-behind is written immediately | A saved XAML file never refers to a method that doesn't exist |
| Preview theme, size, hidden controls and the open tab of a TabControl are view-only | The file only changes when the user changes it |
| Self-contained, unpackaged app with a per-user installer | No runtime prerequisites, no admin rights, clean uninstall |
| ReadyToRun instead of Native AOT | AOT would need source-generated RPC and JSON and the MSVC toolchain for little gain; the render host can't be AOT anyway (Avalonia's runtime XAML loader emits IL and loads user assemblies) |

## 2. System overview

```
┌──────────────────────────── XDesigner.App (WinUI 3, .NET 10, unpackaged) ────────────────────────────┐
│  MainWindow: menus + toolbar in the title bar, Toolbox │ document tabs (DesignSurface) │ Solution and │
│  Outline, Properties/Events │ Output (Output, Build, Run, Events, Error List) │ status bar            │
│  ViewModels (CommunityToolkit.Mvvm) ──► XDesigner.Core (no UI): XamlDocument, commands/undo,         │
│                                         placement, snapping, naming, code-behind writer, projects     │
│  RenderHostManager ── named pipe, StreamJsonRpc + MessagePack ──┐   (one host per framework version     │
│                                                                 │    and project build)                 │
└─────────────────────────────────────────────────────────────────┼──────────────────────────────────────┘
                                                                  ▼
┌──── renderhosts\{avalonia12, avalonia11, winui3, maui11, maui10} (one process per framework version and build) ────┐
│  Avalonia 12 or 11: headless + Skia, the project's App, project assemblies loaded from memory (§14.1–14.4)        │
│  WinUI 3: a cloaked window and XamlReader, run from a shadow copy of the project's build output (§14.5)            │
│  .NET MAUI 11 or 10: the project's MauiProgram, LoadFromXaml through MAUI's WinUI handlers, from a shadow copy     │
│  (§14.6) · each: runtime XAML loader → pixels + layout tree · metadata · effective values                          │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

## 3. Solution structure

| Project | Target | References |
|---|---|---|
| `XDesigner.Protocol` | net10.0 | StreamJsonRpc, MessagePack |
| `XDesigner.Core` | net10.0 | Protocol, Microsoft.CodeAnalysis.CSharp |
| `XDesigner.RenderHost.Common` | net10.0 | Protocol, StreamJsonRpc: what every render host shares (options, pipe server, exit codes, PNG writer, render-once output, frame size limits, C# type names) |
| `XDesigner.RenderHost.Avalonia` (`…Avalonia12.csproj`) | net10.0 exe, win-x64 | Common, Avalonia 12 (Headless, Skia, Markup.Xaml.Loader, Themes.Fluent, Fonts.Inter) |
| `XDesigner.RenderHost.Avalonia11` | net10.0 exe, win-x64 | The same source against Avalonia 11.3 (`VersionOverride`; no `#if` needed: only the tests use `AVALONIA11`) |
| `XDesigner.RenderHost.WinUI` | net10.0-windows10.0.19041.0 exe, x64, WinUI 3 | Common, Windows App SDK |
| `XDesigner.RenderHost.Maui` (`…Maui11.csproj`) | net11.0-windows10.0.19041.0 MAUI app, x64 | Common, Microsoft.Maui.Controls 11 (with its Windows App SDK); links the WinUI host's surface and capture |
| `XDesigner.RenderHost.Maui10` | net10.0-windows10.0.19041.0 MAUI app, x64 | The same source, Microsoft.Maui.Controls 10 (`VersionOverride`) |
| `XDesigner.App` | net10.0-windows10.0.19041.0, x64, WinUI 3 | Core, Protocol, Windows App SDK, CommunityToolkit.Mvvm, CommunityToolkit.WinUI.Controls.Sizers |
| `tests/*` | xUnit v3 | Core tests are pure; the render host tests start the real renderers against `samples/SampleAvaloniaApp` (12), `samples/SampleAvalonia11App` (11, from shared sources), `samples/SampleWinUIApp` (WinUI) and `samples/SampleMauiApp` (MAUI 10 and 11, one project built for both), the last two out of process |

Core and Protocol reference no UI framework, so editing logic is unit-testable and a renderer's framework version never
leaks into the app. The app doesn't reference the render hosts: a build target copies each host's output into
`renderhosts\{folder}\` next to the app, and publishing publishes them there self-contained (§17). Package versions are
central in `Directory.Packages.props` (`AvaloniaVersion`, `Avalonia11Version`, `WindowsAppSdkVersion`, `Maui11Version`, `Maui10Version`). Templates and the toolbox lists are
embedded resources of Core (`Core/Templates`, `Core/Toolbox/toolbox.{family}.json`).

## 4. Protocol (`XDesigner.Protocol`)

Named pipe `xDesigner-{app pid}-{guid}`, StreamJsonRpc with a length-prefixed MessagePack formatter (frames travel as raw
bytes). The host is the server; the app is the client. `IRenderHost`:

| Method | Purpose |
|---|---|
| `HandshakeAsync(protocolVersion)` | Protocol version, framework id (`Avalonia12`) and version, the project's App type, design mode; a protocol mismatch means a stale renderer, a framework mismatch a damaged install |
| `RenderAsync(RenderRequest)` | Render one document: pixels, layout tree, root kind, window title, or an error |
| `GetControlTypesAsync()` | All designable control types (toolbox, content models, default events) |
| `GetPropertiesAsync(type)` / `GetEventsAsync(type)` | Property and event metadata of a type |
| `GetValuesAsync(documentId, designId)` | Effective values of an element in the last render, with their source |
| `GetFontFamiliesAsync()` | Fonts for the FontFamily editor |
| `CloseDocumentAsync(documentId)` / `ShutdownAsync()` | Release a document's render / exit |

`RenderRequest` carries the render copy (§5.4), a scale (screen scale × zoom), the preview theme, an optional preview
size, the document id and a base URI (`avares://{assembly}/{path}`) so `/Assets/…` paths resolve against the project's
resources. `RenderResult` carries premultiplied BGRA pixels (copied straight into a WinUI `WriteableBitmap`) and an
`ElementLayout` tree: design id, bounds, visibility, margin/padding and panel information (Grid row/column offsets,
orientation). `ProtocolInfo.Version` is bumped whenever a DTO or the interface changes.

The design ids of a render copy (§5.4) use a neutral namespace, `urn:xdesigner:design-id`: each host maps it to its own
attached property, whose real namespace depends on the framework and on the host's assembly name.

## 5. Document model (`Core/Xaml`)

### 5.1 `XamlDocument`
Wraps an `XDocument` loaded with whitespace and line info. `Load` remembers encoding, BOM and line endings; `Save`
writes them back atomically (temporary file, then replace). Serialization is custom (`XamlTextWriter`): every start
tag's original text is captured at parse time (`OriginalStartTag`), and so is the source text of every text, comment,
CDATA and processing-instruction node (`OriginalText`, which keeps entities, unescaped `>` and line endings). Unedited
tags and nodes and the prolog are copied verbatim, and an edited tag is rebuilt reusing the original text of its
unchanged attributes. Every *object element*
(not property elements such as `<Grid.RowDefinitions>`) gets an `ElementNode` with an id that is stable for the session
(an annotation on the `XElement`, so it survives moves and undo) and equals the design id in render results. The
document raises `Changed` with the affected ids and whether the structure changed.

### 5.2 Content models
The renderer reports each type's `ContentModel`, which decides where children go:

| Model | Examples | Children |
|---|---|---|
| Children | Grid, StackPanel, Canvas, DockPanel, WrapPanel | any number |
| SingleContent | Window, UserControl, Button, ScrollViewer, Expander | one (replaces text content) |
| SingleChild | Border, Viewbox | one |
| Items | ListBox, ComboBox, TabControl, Menu, TreeView | items, edited with the collection editor (§9.4) |
| Inlines / None | TextBlock, Image, shapes | not a container |

### 5.3 Namespaces
The framework's controls use the default namespace (the document profile's `DefaultNamespace`). Custom controls get
`xmlns:{prefix}="using:{ClrNamespace}"` on the root the first time they're used (`local` for the first custom namespace a
file gets, then the last namespace segment, lowercase). Design-time attributes use `d:` with `mc:Ignorable`. `x:` and
`d:` are the URIs the file already declares (either `x:`, Blend's or MAUI's `d:`; `XamlDocument.XamlNamespace` and
`DesignNamespace`), else the profile's (`x:` 2006, or 2009 for MAUI). A declaration the designer adds always takes a
prefix nothing in the file uses (`FreePrefix`), and an end tag repeats its start tag's name, so a file that binds one
namespace to two prefixes saves well-formed.
MAUI writes custom-control namespaces as `clr-namespace:{ClrNamespace};assembly={Assembly}`.

**Implicit namespaces.** .NET MAUI 11 files may declare no namespaces at all (`<ContentPage x:Class="…">`): its compiler
supplies MAUI's default namespace and `x:`. `ImplicitNamespaces.Parse` reads a file whose root has no namespace (or an
undeclared prefix) with those predeclared and annotates the document; prefix lookups (`PrefixOf`, `NamespaceOf`,
`DefaultNamespaceOf`) know them, so the writer writes `x:Name` and unprefixed MAUI elements as the file has them and no
declaration is ever added. The render copy declares them, since MAUI's runtime loader needs them.

### 5.4 Render copy
`BuildRenderCopy` clones the document, adds `dsn:DesignId.Value="{id}"` to every object element, and applies view-only
overrides (the open tab of a TabControl, controls hidden with the eye icon). The host's `RenderCopySanitizer` then
removes `x:Class`, every event attribute and design ids on non-visual elements (and, in MAUI, on elements whose type it
can't resolve), since only the host can resolve every type. The copy is written with `LineKeepingXml`: every element
and attribute on its line in the file, so the loader's error lines are the file's even when tags span lines (§8.5). Bindings and resources stay; if they fail, the render reports an error. The copy never touches disk.

### 5.5 Formatting of generated XAML
New elements take their siblings' indentation (default four spaces) and the file's line endings. Attributes are
ordered `x:Name`, attached layout properties, size and position, content, the rest, events. Values are written with the
invariant culture.

### 5.6 Values
`Protocol/Metadata/XamlValueText` formats values the same way in both processes (so effective values look exactly like
what the designer writes). `Core/Properties/XamlValueSerializer` validates and normalizes typed input (`"8 4"` →
`"8,4"`, `"#f00"` → `"#FF0000"`, `"red"` → `"Red"`) and rejects anything Avalonia wouldn't load.

## 6. Editing and undo (`Core/Editing`)

Every change is an `IUndoableCommand` (set/clear attribute, insert/remove element, text content, composite). Undo
restores the exact text: removed attributes go back to their position, inserted whitespace is removed with the element.
A move is a remove and an insert of the same `XElement`, so ids survive. `UndoManager` (one per document) runs each
action in one `BatchChanges` scope (one re-render), merges consecutive edits of the same attribute within a second
(typing), tracks the saved state for the dirty marker, and keeps 500 steps. `DesignerActions` is the facade the app
uses: add, delete, move, resize, nudge, set/reset/rename, clipboard, events, structure (reorder, move into a container,
wrap in), items, grid definitions and design size; each builds one composite command.

## 7. Placement, snapping and naming (`Core/Layout`, `Core/Naming`)

### 7.1 Placement
`DropPlanner` finds the deepest container under the pointer that accepts the control and plans the change:

| Container | Drop / move writes |
|---|---|
| Canvas | `Canvas.Left/Top` |
| Grid | `Grid.Row/Column` of the cell under the pointer, the position in the cell as Margin with Left/Top alignment |
| StackPanel, WrapPanel | the insertion index (an insertion line shows where) |
| DockPanel | `DockPanel.Dock` from the nearest edge band, or the fill position |
| Single content/child | the content, if empty |
| Items controls | an item |

Moving to another container removes the attached properties of the old one (Canvas, Grid, DockPanel; MAUI's
AbsoluteLayout and FlexLayout; WinUI's RelativePanel and VariableSizedWrapGrid). Alt keeps the current parent. Pasting
another framework's controls is refused.

Placement code uses the Avalonia/WinUI names above; the profile's `LayoutVocabulary` writes and reads them in the
document's own spelling. For MAUI: `WidthRequest`/`HeightRequest`, `HorizontalOptions`/`VerticalOptions` with
`Start`/`Fill`, and an AbsoluteLayout (reported as a Canvas) positions with one `AbsoluteLayout.LayoutBounds="x,y,w,h"`
whose size part is kept (and resized when it sets a size). A part `AbsoluteLayout.LayoutFlags` makes proportional is
written in DIPs with that flag removed, so the control stays where it was dropped; leaving the AbsoluteLayout keeps the
size the bounds gave as `WidthRequest`/`HeightRequest`. Avalonia and WinUI documents come out byte for byte as before.

### 7.2 Toolbox defaults
`toolbox.{family}.json` (one per framework family, `FrameworkProfile.ToolboxResource`: `toolbox.avalonia.json`, `toolbox.winui.json`,
`toolbox.maui.json`)
lists the toolbox categories and the attributes each control gets when dropped (a Button gets
`Content="button1"`, a TextBox a width, and so on).

### 7.3 Snapping
`SnapEngine` snaps the moving edges (for a resize, only the dragged ones) to sibling edges and centres, the container's
content edges, and an 8-DIP gap, within 6 screen pixels; X and Y independently; red guide lines show why. Optional grid
snapping puts positions and dragged edges on a grid starting at the container's content corner (status bar toggle,
size in Settings). Alt turns snapping off for one drag.

### 7.4 Naming
`NameGenerator` names new controls `camelCase(Type) + n` with the smallest free n. Renames are checked as C#
identifiers that are unique in the document. Avalonia generates a field for every `x:Name`, so code-behind can use
`button1.Content = "Hi";`.

## 8. Design surface (`App/Controls/DesignSurface`)

### 8.1 Layers
Inside a scroll viewer: a stand-in title bar for Windows (headless rendering draws no native chrome), the frame image
(displayed at its DIP size, so it's crisp at any scale), and a transparent adorner canvas that takes all pointer input
and draws hover and selection outlines, resize handles, Grid lines, snap guides, insertion lines and drag ghosts.

### 8.2 Render scheduling
`RenderScheduler` (per document) debounces requests by 40 ms, keeps at most one in flight, sends only the latest, and
drops results older than the one on screen. Nothing re-renders during a drag; zoom rescales the current frame at once
and re-renders sharp. `LayoutIndex` answers hit tests and bounds from the last layout tree.

### 8.3 Interaction
A press becomes a gesture once the pointer moves 4 pixels: a handle resizes, the selection moves, the root or an
unselected panel starts a rubber band. Click selects, Ctrl toggles, Shift adds, Esc selects the parent, Tab cycles
siblings, arrows nudge (Shift ×10) or reorder in stacks, double-click creates the default event handler, Delete
deletes. The right-click menu (shared with the outline, `DesignCommands`): cut, copy, paste, delete, select parent,
bring to front / send to back, wrap in (Grid, StackPanel, Border, Canvas, DockPanel, WrapPanel, ScrollViewer; placement
attributes move to the wrapper), the items or rows/columns editor, and the default event handler.

### 8.4 Preview
Per document and view-only: **Theme** (Default/Light/Dark), **Size** (design size, presets, custom; View ▸ Keep this
preview size writes `Width/Height` for a Window or `d:DesignWidth/Height` otherwise), **Zoom** (25–400 %, Fit, Ctrl +
wheel).

### 8.5 Errors
A failed render keeps the last good frame dimmed under a red banner with the message, the line (the file's: the render
copy keeps every element on its line, §5.4), and the offending control type. The banner offers Undo (the last change) and, when the renderer itself stopped, Retry. A file reloaded
from disk keeps showing the previous frame until its own render arrives. A render that takes over 10 s stops the
renderer (§14.2).

## 9. Panels

### 9.1 Toolbox
Categories from the framework's toolbox list (Common, Layout, Containers & Items, Input & Pickers, Text, Shapes; WinUI also
Status & Info, MAUI Collections), then **Project**
(the project's own controls) and **Other**. Built-in entries missing from the renderer's metadata are hidden. Search
filters all categories; drag to the surface, or double-click to add to the selected container.

### 9.2 Properties
Header: the name (editable, F4 focuses it) and type. Categories (Common, Layout, Appearance, Text, Behavior, Window,
Misc), A–Z sorting, search, "only set" and "advanced" filters. Set properties are bold with a reset button; others show
the effective value from the renderer, greyed. Editors by kind: text (with a multi-line popup), numbers (Up/Down step),
check boxes, enums, thickness and corner radius (with per-side popup), colors, fonts, grid lengths, images (§12).
Bindings and resources show read-only with reset. With several controls selected, shared properties are shown and set
together. Pending edits are committed before saving and closing.

### 9.3 Events
Events by category; each row has the handler name, a list of compatible methods already in the code-behind (read with
Roslyn each time), and a button to create the default handler or jump to it. Enter connects a typed name (creating the
method if needed); an empty box disconnects.

### 9.4 Collection editor
For TabControl (TabItem, Header; a new tab gets a `Grid` to drop into), Menu/ContextMenu (MenuItem tree), TreeView
(TreeViewItem tree), ComboBox and ListBox: add, add sub-item, remove, reorder, edit the text. One undo step; kept items
keep their markup. The TabItem holding the selection is shown on the surface through a view-only `SelectedIndex`.

### 9.5 Grid rows and columns
One line per row or column: Auto, * (share) or pixels, with optional Min/Max. Written in the compact form
(`RowDefinitions="Auto,*,40"`) unless Min/Max are used; in WinUI, which has no compact form, always as
`<Grid.RowDefinitions>` (the definitions in the framework's namespace, also in a Grid subclass). Children follow their row or column; controls in a deleted one
move to the nearest earlier one; spans are clamped.

### 9.6 Document outline
A TreeView of the elements (name and type, or `[Type]` with a caption). Selection syncs both ways. Drag and drop: the
top or bottom quarter of a row means before/after, the middle means inside. The drag is started by hand
(`StartDragAsync`) and dropped on the outline's root grid rather than using TreeView's own item drag, which reorders
its collections by itself and auto-scrolls; reordered rows are removed and re-inserted because TreeView ignores
`Move`. The eye icon hides a control on the surface only.

### 9.7 Output
Tabs: Output (renderer, files, errors), Build (dotnet build output), Run (the running app's output), Events (every
handler created, connected or disconnected). Each keeps the last 500 lines. Log lines are objects, not strings: WinUI
lists of identical strings looped UI Automation.

### 9.8 Status bar
Renderer state and Avalonia version, file path, selection bounds, zoom, grid-snap toggle.

## 10. Event handlers (`Core/CodeGen`)

### 10.1 Creating a handler
1. Resolve the event (the type's default event for a double-click).
2. Give the control an `x:Name` if it has none.
3. Name the method `{name}_{Event}`.
4. Find `{file}.axaml.cs` and the class named by `x:Class` (offering to create them when missing).
5. `CodeBehindWriter` parses the file with Roslyn: an existing method with a compatible signature is reused; otherwise
   `private void {name}(object? sender, {Args} e) { }` is inserted at the end of the class, keeping the file's encoding,
   BOM, line endings and indentation, adding a `using` if needed; the write is atomic.
6. Only then is the event attribute set in the XAML (an undoable command).
7. A notification names the method, event, file and line, with Open in editor (§15.3) and Copy name.

### 10.2 Rules
Undo removes only the XAML link; the method stays. Renaming or deleting a control never changes C#.

## 11. Projects, build and run (`Core/Project`)

### 11.1 Opening
`.csproj`, `.sln` or `.slnx` opens as a `SolutionModel` (a project on its own is a solution of one): every C# project is
evaluated in parallel with `dotnet msbuild -getProperty -getItem` (TargetPath, AssemblyName, RootNamespace,
TargetFramework, OutputType, UseWinUI, UseMaui and the package references with their evaluated versions, so versions
from `Directory.Build.props` or central package management count; a multi-targeted project is read for its desktop
framework, a MAUI one for its Windows framework, and either becomes its `BuildFramework`; a WinUI project without a
platform of its own for `x64`, the WinUI renderer's, since WinUI has no AnyCPU build; and whether a WinUI or MAUI app is
packaged, from `WindowsPackageType`). `FrameworkDetector` finds each project's framework and version: from the build
output when there is one (WinUI only with `UseWinUI`: a WPF app may use the Windows App SDK too), else from that
evaluation. Projects may mix frameworks and versions; each document belongs to the
innermost project containing it and is drawn by that project's framework version. The startup project (what Run starts,
what Project ▸ Add adds to) is the first designable app, and can be changed in Solution Explorer (to a designable
project; the choice is kept per solution in the settings). Opening waits for a running build, one open at a time; a
solution with no readable project leaves the open one as it was. A file whose framework isn't its project's (a WPF
window, MAUI's WinUI `Platforms\Windows\App.xaml`, which isn't listed) is drawn without that project's build. A file opened on its
own finds its project and last build heuristically (`ProjectOutputLocator`) so custom controls still render. Projects
on a framework or version the designer can't render are explained (`DetectedFramework.Problem`). The .NET SDK is run
with English output, no logo, no telemetry and the classic logger, so its output can be parsed.

### 11.2 Build
`dotnet build` of the solution (or the single project) in its folder (its `global.json` picks the SDK), output streamed
to Output ▸ Build, errors and warnings parsed into Output ▸ Error List (double-click one to go to it). WinUI projects
are built with their platform (`-p:Platform=x64`) and MAUI projects for Windows only (`-f net11.0-windows…`, so no
Android or iOS workload is needed); a solution that contains either is built project by project
(`SolutionModel.BuildSteps`, each step with the solution's `$(SolutionDir)` and friends), since a solution's platform
mapping rarely gives WinUI a platform. Builds run on open, on Rebuild and before Run, and can be cancelled (the Rebuild
item, and closing the designer); unsaved files of the solution are saved first (one that can't be is reported, and the
build uses the file on disk). A build restarts the renderers of the projects whose output changed (the newest of its
assemblies, deps and resource files: a rebuilt library is copied into the apps that use it), also for the projects
that built when another failed. Because the renderer loads project assemblies from memory, builds outside the designer
(VS Code, Visual Studio) never hit locked files, and the designer reloads after them.

### 11.3 Run
Save, build, then `AppRunner`: `dotnet run --no-build` (WinUI with `--property:Platform`, MAUI with `-f`); output to
Output ▸ Run; Stop kills the process tree. A packaged WinUI or MAUI app runs as Visual Studio deploys it: its build output (a loose package
layout with `AppxManifest.xml`) is registered (`Add-AppxPackage -Register`) and activated by its app user model id
through `IApplicationActivationManager`, which returns the process to watch and stop; its console output isn't
captured. Registering an unsigned package needs Developer Mode (read from the registry, never changed); without it
the packaged build's exe is started directly, without package identity, and the Run output says so, unless the app
uses the Windows App SDK installed on Windows (no `Microsoft.WindowsAppRuntime.dll` in its output), which only works as
a package: then Run asks for Developer Mode. An app that exits while it starts is reported as such. One running
instance; stopped before builds and when the designer closes.

### 11.4 New project
A framework version (Avalonia 12 or 11, WinUI 3 or .NET MAUI 11 or 10, the last two unpackaged or packaged), a name, a
folder and a title create a desktop app from Core's embedded templates (no `dotnet new`; both Avalonia versions share
`AvaloniaApp`, with the profile's `TemplateVersion`; WinUI has `WinUIApp`, MAUI `MauiApp`): a csproj with an images
item, `Program.cs` with an explicit `Main` (WinUI and MAUI: `DISABLE_XAML_GENERATED_MAIN`), `App` and `MainWindow` (MAUI:
`MauiProgram`, `MainPage`, and the Windows `App` in `Platforms\Windows`) with code-behind, `app.manifest`, `global.json`
and `.gitignore`; a packaged app also gets `Package.appxmanifest` (WinUI with generated logo PNGs, MAUI with a
`MauiIcon` its build turns into them). A MAUI app targets Windows only. It is then built and its main window opened.

### 11.5 New items
Add window / Add user control (and Add page in WinUI) create `{Name}.axaml` (`.xaml` in WinUI and MAUI) and its code-behind in
the project's namespace from the framework's item templates (`Templates/Items`, `Templates/WinUIItems`,
`Templates/MauiItems`) and open them. MAUI offers a content page and a content view (windows are made in code).

### 11.6 File watching
Open XAML files are watched. A change on disk that differs from what the designer last loaded or saved reloads
silently when the document is clean, and asks when it has unsaved changes. A file deleted or renamed elsewhere (a
branch switch) stays open and is said to be gone; saving it asks before creating it again. Every project's build
output is watched for builds made elsewhere. The recent list is changed as the settings file has it, so two designers
open don't lose each other's entries.

## 12. Images (`Core/Assets`)
Image properties get a picker: the project's images under `Assets/` with thumbnails, Browse…, and Remove. Browse copies
the file into `Assets/` (an identical file is reused; a different file with the same name gets a numbered name), adds
the framework's Assets item to the project if nothing covers Assets (a text edit that keeps the rest of the file byte
for byte), sets the property, and rebuilds without saving other files, so the renderer has the image. Avalonia:
`<AvaloniaResource Include="Assets\**" />` and `/Assets/{file}`. WinUI: `<Content Include="Assets\**"
Exclude="@(Content)" CopyToOutputDirectory="PreserveNewest" />` (the exclusion keeps files the project already lists,
such as Visual Studio's logos, from being packaged twice) and `ms-appx:///Assets/{file}`. MAUI: the images folder is
`Resources\Images` (the profile's `AssetsFolder`), `<MauiImage Include="Resources\Images\*" />`, the bare file name, and
imported files renamed to the lowercase letters, digits and `_` MAUI requires. Asset paths that would resolve outside
the project are rejected. Files outside a project get a message instead.

## 13. Clipboard (`Core/Editing/DesignerActions.Clipboard.cs`)
Copy writes the top-most selected elements, re-indented, inside an `<xd:Fragment>` with the namespace declarations they
need and the source document's id, as a custom clipboard format plus plain text. Paste accepts that or plain XAML,
finds a container from the selection, renames clashing names, strips event attributes when the fragment came from
another document, drops attached properties of other panel kinds and offsets positions. Cut is copy plus delete.

## 14. Render host (`XDesigner.RenderHost`)

### 14.0 One host per framework version
`XDesigner.RenderHost.Avalonia` is built twice: against Avalonia 12 (`XDesigner.RenderHost.Avalonia12.exe`) and against
Avalonia 11.3 (`XDesigner.RenderHost.Avalonia11.exe`; every API it uses exists in both, so the source has no `#if`);
Avalonia 11.1 and 11.2 projects run on the 11.3 host. `XDesigner.RenderHost.WinUI.exe` draws WinUI 3 (§14.5), and
`XDesigner.RenderHost.Maui11.exe`/`Maui10.exe` draw .NET MAUI (§14.6; one source built against each MAUI version, as
each pins its own Windows App SDK). What doesn't depend on a framework is in `XDesigner.RenderHost.Common`: the command
line, the pipe server and its exit codes, the PNG writer and render-once output, the frame size limits (`FrameScale`)
and C# type names for handlers. §14.1–14.4 describe the Avalonia host; §14.5 and §14.6 how the others differ.

### 14.1 Startup
`XDesigner.RenderHost.Avalonia{11|12}.exe --pipe {name} --parent-pid {pid} [--assembly {project dll}] [--no-user-app]` (also
`--render {file} --out {png}` for one-off renders, and `--version`). It exits when the app process does. Design mode is
switched on before the project's App is created (`Design.IsDesignMode` has only an internal setter in Avalonia 12, so
it's set by reflection; the runtime loader's `DesignMode` flag doesn't set it). The project's assemblies load from
memory, with their dependencies resolved through the project's `deps.json`. The AppBuilder is the project's own
`BuildAvaloniaApp()` when it has one, else its App type, else a default Fluent app; then headless with Skia, without a
lifetime, so the project's startup code doesn't open windows. If the project's App fails, exits or hangs (45 s) while
starting, the app restarts the host with `--no-user-app` (every host). A project asking for a newer version of an
assembly the host has (Avalonia 12.2 on the 12.1 host) gets the host's, and the designer says the renderer is older.
Native libraries load from a per-process copy in `%TEMP%\xDesigner-native`, so the build output stays unlocked.

### 14.2 Rendering
`XamlRenderer` loads the render copy with `AvaloniaRuntimeXamlLoader` (design mode, the project as local assembly, the
document's base URI), hosts the root (a Window at its size or the preview size; anything else inside a host window at
its design size, default 800×450), applies the theme, runs layout, renders to a `RenderTargetBitmap` at the requested
scale, and walks the visual tree for the layout of every element with a design id. Load errors report line and column;
the offending custom control is found from the exception's stack. The last render stays alive for value queries.

**Watchdog:** a control stuck in a loop blocks the host's UI thread, so the app gives each render 10 seconds, then stops
that host and says so. The stop counts towards the crash limit and doesn't re-render by itself, so a control that hangs
every time can't restart the renderer in a loop; the next edit, undo or Retry starts a fresh host.

### 14.3 Metadata
Public `Control` types from Avalonia and the project (abstract ones not creatable; one that fails to describe is
left out, not the whole list; the project's libraries are loaded at start, so theirs are found); content model from `[Content]` and
base types; properties from the Avalonia property registry plus the attached properties of Grid, Canvas, DockPanel,
ToolTip and ScrollViewer; routed and CLR events with their handler signatures. Cached for the host's lifetime.

### 14.4 Values
For an element of the last render: each property's value and source (local, style, template, inherited, default) from
`AvaloniaObjectExtensions.GetDiagnostic`, formatted like the designer writes values (§5.6).

### 14.5 WinUI host
WinUI can't render headless or load a second app's XAML into a process, so the WinUI host is a WinUI app of its own
that never shows a window:
- **Shadow folder.** It runs from a copy of the project's build output with the host's files on top
  (`Core/Rendering/ShadowHostFolder`, prepared by `RenderHostManager` under `%LOCALAPPDATA%\xDesigner\shadow`): host
  files are hard-linked, the project's are copied (not `AppX\`, `publish\` or `AppPackages\`), so rebuilding never meets a
  locked file. There, `ms-appx:///` URIs, compiled XAML (`.xbf`) and plain files resolve as in the user's app. The
  project's resource index can't stand in for the host's (WinUI then fails to start), so what it would resolve is read
  from the project's files instead (`ProjectResources`): `x:Uid` strings from `Strings\{language}\Resources.resw`, and
  `ms-appx:///` images that exist only scale-qualified (`logo.scale-200.png`). The project's `App.xbf` is left out: WinUI
  would load it into whichever Application runs. A folder belongs to the host process started from it, is held by an open
  lock file, and is deleted when that process is; each start removes the folders nobody holds (older than a minute).
- **Application.** `HostApp` (no App.xaml; started by `Program.Main` with `Application.Start`) implements
  `IXamlMetadataProvider` over the controls library's provider, its own generated one (found by reflection) and the
  project's, so `XamlReader` knows every type; the project's libraries are loaded at start, so theirs are known before a
  file uses them. It merges `XamlControlsResources` (with the project's settings on it, `ControlsResourcesVersion`) and the
  `<Application.Resources>` of the project's App.xaml (a ResourceDictionary, or resources written straight in), read from
  the project folder (`--project`); App.xaml's `RequestedTheme` is what the Default preview theme draws in. The project's
  App class never runs. With `--no-user-app` the project's assembly isn't loaded at all (its XAML provider runs its code,
  a framework-dependent app's Windows App SDK bootstrapper among it).
- **Design ids.** `DesignId` is an attached property on a class that isn't a DependencyObject (the XAML compiler would
  otherwise take its inherited GetValue/SetValue); `TypeInfoAnchor.xaml` makes the compiler generate its type info.
- **Rendering.** Work runs on the UI thread one piece at a time (`HostApp.InvokeTaskAsync`). The sanitizer turns a
  `Window` root into a Grid standing for its client area (a Window isn't an element; `<Window.Content>` is unwrapped into
  it, and a bound title isn't shown as text) and removes what runtime XAML can't have: `x:Class`, event attributes,
  `{x:Bind}`, `x:Load`, `x:Phase`, `x:DataType`, `x:DefaultBindMode`, `x:DeferLoadStrategy`, `x:ClassModifier`,
  `x:FieldModifier`, and the `d:` namespace (after reading the design size). The root lays out in a Grid of the design
  size (keeping its own Width, Min/Max and Margin), in a Viewbox sized at the requested scale (zoom is crisp;
  RasterizationScale doesn't change a RenderTargetBitmap's size), inside a Border with the theme's page background, on a
  canvas in a cloaked, never-activated tool window. After layout it waits for images (and SVGs) to decode and reach the
  screen, at most 1.5 s, and never again for one that failed or timed out; then it captures with `RenderTargetBitmap`.
  Layout comes from `TransformToVisual`, from the design area; Grid offsets include Padding and Row/ColumnSpacing. A
  Window's root is reported as `Microsoft.UI.Xaml.Window` without panel info, with its Title among its values; Pages
  are reported as UserControls. A render that arrives after its document closed is refused.
- **Metadata and values.** Types deriving from UIElement, plus Window; content models from `[ContentProperty]` and base
  types; dependency properties (a settable CLR property with a static `{Name}Property`), plain settable properties and
  attached properties (Grid, Canvas, RelativePanel, VariableSizedWrapGrid, ToolTipService, ScrollViewer); events with
  `RoutedEvent` fields marked routed. Values come from `ReadLocalValue`/`GetValue`, so the source is Local or Default
  only. Fonts are read from the registry (WinUI has no font list).
- **Shared code.** The surface (`RenderSurface`: the cloaked window and the one-at-a-time UI work queue) and the
  capture (`FrameCapture`: the frame, waiting for layout, deferred work and images, `RenderTargetBitmap`) are compiled
  into the MAUI hosts too, as linked source.

### 14.6 MAUI hosts
MAUI on Windows draws with WinUI, so the MAUI hosts reuse the WinUI host's approach, as MAUI apps of their own:
- **Startup.** The host's WinUI Application is MAUI's (`MauiWinUIApplication`, so MAUI's WinUI resources and type
  information are there). Its `CreateMauiApp()` runs the project's own `MauiProgram.CreateMauiApp()` (found by
  reflection), so the project's App, styles, fonts and handlers draw the pages; without one (or with `--no-user-app`) a
  plain `DesignApp`. MAUI's startup opens the App's main window: `WindowHider`, a thread-local CBT hook installed for the
  host's lifetime, cloaks every top-level window as it is created and refuses every activation, so neither that window
  nor one the project opens later appears or takes focus. The window's page is then replaced with an empty one (the
  project's MainPage would keep running unseen), and its window-scoped `IMauiContext` is what pages are drawn with. When
  the main page fails at design time, the App (its styles and fonts) is kept and the host opens a window of its own.
- **Shadow folder.** As for WinUI, plus the project's resource index used as the host's (`{Project}.pri`, or a packaged
  build's `resources.pri`, copied over `{Host}.pri`, `HostUsesProjectResourceIndex`): MAUI writes images as
  scale-qualified files (`logo.scale-200.png`) that only the project's index maps `logo.png` to, and both apps keep their
  WinUI App at `Platforms\Windows\App.xaml`.
- **Rendering.** `MauiSanitizer` gives files without namespaces (MAUI 11) MAUI's, writes elements of MAUI's global
  namespace (which MAUI 11's runtime loader doesn't know) in MAUI's own namespace or the project control's
  `clr-namespace:`, gives `clr-namespace:` declarations without an assembly the one the namespace is in (MAUI's compiler
  assumes the project's; its runtime loader would look in MAUI's), and removes `x:Class`, event attributes, the `d:`
  namespace and the design ids of elements that aren't BindableObjects or that it can't resolve. Names resolve as MAUI's
  loader does: as written, with the `Extension` suffix (`StaticResource`), and generic (`OnPlatform x:TypeArguments`).
  The root's type is created and loaded with `LoadFromXaml` (a Window's page is drawn, inside the Window the layout
  reports), turned into WinUI elements with `ToPlatform(context)`, drawn with `FrameCapture` after MAUI's own image
  loading has given each image its source (not waiting again for one that never arrived), and the theme is set through
  `Application.UserAppTheme`. Bounds come from each element's WinUI view (`TransformToVisual`, so TranslationX/Y, Scale
  and Rotation count), else from adding up Frames; MAUI's Grid doesn't publish row and column sizes, so they are worked
  out from the definitions and the visible children. AbsoluteLayout is reported as a Canvas, the stack layouts as
  StackPanels and FlexLayout as a WrapPanel. A page that's replaced has all its handlers disconnected.
- **Metadata and values.** VisualElement types (and Window); content models from `[ContentProperty]` and base types (a
  project's own ContentView or ContentPage is never a drop target); bindable properties (`{Name}Property` fields) and the
  attached properties of Grid, AbsoluteLayout, FlexLayout, ToolTipProperties and SemanticProperties; LayoutOptions offered
  as `Start`/`Center`/`End`/`Fill`; events with `object? sender`. Values use `IsSet` and what the file writes: a set value
  the XAML writes is Local, one it doesn't (a style's) is Style, an unset one Default. Fonts are the project's registered
  aliases (MAUI's font registrar's registrations, read by reflection) and then the installed ones.
- **Size.** The Windows App SDK parts MAUI brings and the renderer never uses (AI and machine learning, Search, Widgets)
  are excluded, about 55 MB per host; Windows App SDK 1.8 (MAUI 10) copies their native files itself, so they're
  deleted after the build.

## 15. App

### 15.1 Shell
`MainWindow` (split into partial files by area: Chrome, Editing, Design, Images, Notify, Preview, Project, Run,
Settings, Help) with content extended into the title bar, which holds the menus (File, Edit, View, Project, Help) and
the toolbar. The top of the right column holds **Solution Explorer** (the solution's projects with their framework,
startup in bold, folders, XAML and C# files with code-behind under its XAML file; double-click opens XAML in the
designer and other files in the code editor; right-click sets the startup project) and the Document Outline, as tabs.
Output has an **Error List**. View ▸ Toolbox and Output show or hide those panes (saved in settings). Code is opened
through `ICodeEditorService` (today the external editor of §15.3): the seam where an editor built into the designer
will go. A notification strip under the title bar slides open for confirmations, warnings and errors (actions keep
it open longer; decisions stay). View models: `ShellViewModel` (documents, solution and startup project, build/run,
settings, logs), `DocumentViewModel` (document, undo, selection, preview state, render scheduling),
`OutlineNodeViewModel`, `SolutionNodeViewModel` (Solution Explorer rows) and `DiagnosticRow` (Error List rows, paths
relative to the solution). Run, Rebuild and Add are disabled while they can't act (busy, or a project the designer
can't design); the editing shortcuts act on the design only from the design surface and the outline. Last-chance
handlers report unexpected exceptions in Output, a notification and `%LOCALAPPDATA%\xDesigner\errors.log` (capped at
512 KB) instead of closing the app with unsaved work. A named mutex (`xDesigner.Running`) lets the installer see that
the app is open.

### 15.2 Renderer management
`RenderHostManager`: one host per `HostKey` (framework profile and project assembly; "no project" is one more), the
profile's exe from `renderhosts\{HostFolder}\` (a WinUI or MAUI host from a fresh shadow folder of the project's
output, §14.5, §14.6, owned by the process started from it; folders left by a designer that was ended are removed when
the next one starts), started on first use (off the manager's lock), restarted after a crash at most three times a
minute (then Retry; a dead host's process, pipe and folder are released), restarted after a build that changed the
project's output, and stopped a few seconds after no open document uses it (its files closed, another solution opened).
Requests interrupted by a planned restart are sent once more to the new host; metadata requests give up after 15 s, and
a failed one is asked again. A project on a newer framework version than its renderer gets a notification. `RenderHostConnection` (Core) starts the process, connects,
handshakes, keeps recent stderr for diagnostics, and kills the process if a shutdown request doesn't end it.

### 15.3 Settings
`%LOCALAPPDATA%\xDesigner\settings.json` (`Core/Settings`): recent files and projects, the code editor (automatic, VS
Code, Visual Studio, Windows default or a custom command with `{file}` and `{line}`), the preview theme for new files,
snapping, "show advanced", pane sizes and visibility, and each solution's startup project. Unreadable settings fall back
to defaults. File ▸ Settings… edits them.
Help ▸ Open the settings and error log folder opens the folder.

### 15.4 Keyboard
Listed in the README. Accelerators that would steal keys from text boxes (Ctrl+C/V/X/Z/Y, Delete) are handled at window
level only when no text box has focus.

## 16. Errors and robustness
| Failure | Handling |
|---|---|
| XAML or custom control error | Error banner, last good frame dimmed, Undo offered |
| Control hangs while drawing | Renderer stopped after 10 s, banner with Retry (§14.2) |
| Renderer crashes | Restarted on the next request (max 3 a minute), interrupted request retried |
| Project App fails, exits or hangs while starting in the renderer | Host restarted with the default theme, warning in Output |
| Build fails | Build output shown; projects that did build are reloaded, the others keep the previous assemblies |
| File deleted or renamed while open | Notification; saving asks before creating it again |
| Code-behind missing or unparseable | Message; the XAML isn't changed |
| File can't be written | Message; the document stays dirty |
| File changed on disk | §11.6 |
| Unexpected exception | Reported (Output, notification, errors.log); the app keeps running |

## 17. Build and release
`global.json` pins the .NET 11 SDK (the MAUI 11 host needs it; it builds the .NET 10 projects too), and the MAUI
workload for Windows is needed for the MAUI hosts and sample. `Directory.Build.props` sets the version, product metadata, nullable, the
recommended analyzers and code-style enforcement, and treats warnings as errors in Release. The app is unpackaged and
self-contained (Windows App SDK included), x64 only. Release publishing (`dotnet publish src/XDesigner.App -c Release`)
is self-contained and ReadyToRun, and publishes each render host self-contained into `renderhosts\{folder}\`. The
installer has one component per render host, so users can leave out frameworks they don't design. To keep it lean, the
app and the WinUI host exclude the Windows App SDK's AI, ML, Search and Widgets parts, use the non-WPF build of
`Microsoft.VisualStudio.Threading` (its Windows build would pull in all of WPF; `build\VsThreadingWithoutWpf.targets`),
and nothing publishes `.pdb` files. The WinUI and MAUI hosts are built and published for x64 (the app's
`RenderHostProject` items carry each host's platform). `.editorconfig` turns off CsWinRT1032 (collection expressions
and trimming), which matters only to trimmed or AOT-compiled apps.
`EnableMsixTooling` makes the unpackaged app's resources (`xDesigner.pri`, compiled XAML) part of the publish.

`build\publish.ps1` builds, tests, publishes, checks the output and compiles `installer\xDesigner.iss` (Inno Setup): a
per-user install in `%LOCALAPPDATA%\Programs\xDesigner` that closes a running designer first and clears the install
folder on upgrade, and whose uninstaller removes the install folder, shortcuts, uninstall entry and
`%LOCALAPPDATA%\xDesigner`. Users' projects are never touched. `tools\make-notices.ps1` regenerates
`THIRD-PARTY-NOTICES.md` from the restored packages.

## 18. Testing
- `tests/XDesigner.Core.Tests`: the document model (byte-identical round trips, every command's undo), placement,
  snapping, naming, values, clipboard, code-behind writing (namespaces, encodings, line endings, line numbers),
  project templates and reading, settings, assets.
- `tests/XDesigner.RenderHost.Tests`: the Avalonia 12 renderer against `samples/SampleAvaloniaApp` (pixel sizes, layout,
  metadata, custom controls, errors, project images, the pipe connection and crash handling).
- `tests/XDesigner.RenderHost.Avalonia11.Tests`: the same test sources (`AVALONIA11`) against the Avalonia 11 renderer
  and `samples/SampleAvalonia11App`, the same app on Avalonia 11.
- `tests/XDesigner.RenderHost.WinUI.Tests`: the WinUI renderer over the pipe, started from a shadow folder of
  `samples/SampleWinUIApp` as the app starts it (a WinUI host can't run inside a test process): windows, pages, the
  project's controls, App.xaml resources and images, compiled-binding removal, layout, metadata, values, events, fonts.
- `tests/XDesigner.RenderHost.Maui.Tests`: both MAUI renderers over the pipe, each from a shadow folder of
  `samples/SampleMauiApp`'s build for its .NET version: the project's App, controls and images, MAUI 11 files without
  namespaces, layout (Grid rows, AbsoluteLayout), errors, metadata, values, events.
- Tests marked `Category=Slow` run `dotnet build` or MSBuild and need the SDK and NuGet packages.
- The UI is checked by hand in the running app before a release: designing, events and Run on a new project; every
  panel and editor; preview; images; error and hang handling; external edits; install, upgrade and uninstall.
