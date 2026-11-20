# xDesigner architecture

How xDesigner works, for contributors. Code comments refer to the sections here as `§n.n`.

## 1. Purpose

xDesigner is a WinForms-style visual designer for C# UI frameworks: today [Avalonia UI](https://avaloniaui.net/) 11 and
12 and WinUI 3 (Windows App SDK, packaged or unpackaged); .NET MAUI is recognised and comes next. You drag controls from a toolbox onto a form, edit them in a
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
│  MainWindow: menus + toolbar in the title bar, Toolbox │ document tabs (DesignSurface) │ Outline,      │
│  Properties/Events │ Output (Output, Build, Run, Events) │ status bar                                  │
│  ViewModels (CommunityToolkit.Mvvm) ──► XDesigner.Core (no UI): XamlDocument, commands/undo,         │
│                                         placement, snapping, naming, code-behind writer, projects     │
│  RenderHostManager ── named pipe, StreamJsonRpc + MessagePack ──┐   (one host per framework version     │
│                                                                 │    and project build)                 │
└─────────────────────────────────────────────────────────────────┼──────────────────────────────────────┘
                                                                  ▼
┌────────── renderhosts\avalonia12, renderhosts\avalonia11 (.NET 10, one process per project build) ─────────┐
│  Avalonia 12 or 11 headless + Skia · the project's App (theme, fonts) · project assemblies loaded from memory │
│  XamlRenderer (runtime XAML loader, design mode) → pixels + layout tree · metadata · effective values       │
└─────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

## 3. Solution structure

| Project | Target | References |
|---|---|---|
| `XDesigner.Protocol` | net10.0 | StreamJsonRpc, MessagePack |
| `XDesigner.Core` | net10.0 | Protocol, Microsoft.CodeAnalysis.CSharp |
| `XDesigner.RenderHost.Common` | net10.0 | Protocol, StreamJsonRpc: what every render host shares (options, pipe server, exit codes, PNG writer, render-once output, frame size limits, C# type names) |
| `XDesigner.RenderHost.Avalonia` (`…Avalonia12.csproj`) | net10.0 exe, win-x64 | Common, Avalonia 12 (Headless, Skia, Markup.Xaml.Loader, Themes.Fluent, Fonts.Inter) |
| `XDesigner.RenderHost.Avalonia11` | net10.0 exe, win-x64 | The same source, `AVALONIA11` defined, Avalonia 11.3 (`VersionOverride`) |
| `XDesigner.RenderHost.WinUI` | net10.0-windows10.0.19041.0 exe, x64, WinUI 3 | Common, Windows App SDK |
| `XDesigner.App` | net10.0-windows10.0.19041.0, x64, WinUI 3 | Core, Protocol, Windows App SDK, CommunityToolkit.Mvvm, CommunityToolkit.WinUI.Controls.Sizers |
| `tests/*` | xUnit v3 | Core tests are pure; the render host tests start the real renderers against `samples/SampleAvaloniaApp` (12), `samples/SampleAvalonia11App` (11, from shared sources) and `samples/SampleWinUIApp` (WinUI, out of process) |

Core and Protocol reference no UI framework, so editing logic is unit-testable and a renderer's framework version never
leaks into the app. The app doesn't reference the render hosts: a build target copies each host's output into
`renderhosts\{folder}\` next to the app, and publishing publishes them there self-contained (§17). Package versions are
central in `Directory.Packages.props` (`AvaloniaVersion`, `Avalonia11Version`, `WindowsAppSdkVersion`). Templates and the toolbox lists are
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
`xmlns:{prefix}="using:{ClrNamespace}"` on the root the first time they're used (`local` for the project's namespace,
otherwise the last namespace segment). Design-time attributes use `d:` (the profile's `DesignNamespace`) with
`mc:Ignorable`; `x:` is the profile's XAML namespace (2006, or 2009 for MAUI). Names are read under either `x:` URI.

### 5.4 Render copy
`BuildRenderCopy` clones the document, adds `dsn:DesignId.Value="{id}"` to every object element, and applies view-only
overrides (the open tab of a TabControl, controls hidden with the eye icon). The host's `RenderCopySanitizer` then
removes `x:Class`, every event attribute and design ids on non-visual elements, since only the host can resolve every
type. Bindings and resources stay; if they fail, the render reports an error. The copy never touches disk.

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

Moving to another container removes the attached properties of the old one. Alt keeps the current parent.

### 7.2 Toolbox defaults
`toolbox.{family}.json` (one per framework family, `FrameworkProfile.ToolboxResource`; `toolbox.avalonia.json` today)
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
Per document and view-only: **Theme** (Default/Light/Dark), **Size** (design size, presets, custom; View › Keep this
preview size writes `Width/Height` for a Window or `d:DesignWidth/Height` otherwise), **Zoom** (25–400 %, Fit, Ctrl +
wheel).

### 8.5 Errors
A failed render keeps the last good frame dimmed under a red banner with the message, the line, and the offending
control type. The banner offers Undo (the last change) and, when the renderer itself stopped, Retry. A file reloaded
from disk keeps showing the previous frame until its own render arrives. A render that takes over 10 s stops the
renderer (§14.2).

## 9. Panels

### 9.1 Toolbox
Categories from the framework's toolbox list (Common, Layout, Containers & Items, Input & Pickers, Text, Shapes), then **Project**
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
(`RowDefinitions="Auto,*,40"`) unless Min/Max are used. Children follow their row or column; controls in a deleted one
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
evaluated in parallel with `dotnet msbuild -getProperty` (TargetPath, AssemblyName, RootNamespace, TargetFramework,
OutputType; a multi-targeted project is read for its desktop framework, else its Windows one; a WinUI project without a
platform of its own for this machine's, `x64` or `ARM64`, since WinUI has no AnyCPU build, and whether it is packaged,
from `WindowsPackageType`), and `FrameworkDetector`
finds each project's framework and version. Projects may mix frameworks and versions; each document belongs to the
innermost project containing it and is drawn by that project's framework version. The startup project (what Run starts,
what Project ▸ Add adds to) is the first designable app, and can be changed in Solution Explorer. A file opened on its
own finds its project and last build heuristically (`ProjectOutputLocator`) so custom controls still render. Projects
on a framework or version the designer can't render are explained (`DetectedFramework.Problem`). The .NET SDK is run
with English output, no logo, no telemetry and the classic logger, so its output can be parsed.

### 11.2 Build
`dotnet build` of the solution (or the single project) in its folder (its `global.json` picks the SDK), output streamed
to Output › Build, errors and warnings parsed into Output › Error List (double-click one to go to it). WinUI projects
are built with their platform (`-p:Platform=x64`), and a solution that contains one is built project by project
(`SolutionModel.BuildSteps`), since a solution's platform mapping rarely gives WinUI a platform. Builds run on
open, on Rebuild and before Run; unsaved files of the solution are saved first. A successful build restarts the
renderers of the projects whose assemblies changed. Because the renderer loads project assemblies from memory, builds outside the
designer (VS Code, Visual Studio) never hit locked files, and the designer reloads after them.

### 11.3 Run
Save, build, then `AppRunner`: `dotnet run --no-build` (WinUI with `--property:Platform`); output to Output › Run;
Stop kills the process tree. A packaged WinUI app runs as Visual Studio deploys it: its build output (a loose package
layout with `AppxManifest.xml`) is registered (`Add-AppxPackage -Register`) and activated by its app user model id
through `IApplicationActivationManager`, which returns the process to watch and stop; its console output isn't
captured. Registering an unsigned package needs Developer Mode (read from the registry, never changed); without it
the packaged build's exe is started directly, without package identity, and the Run output says so. One running
instance; stopped before builds and when the designer closes.

### 11.4 New project
A framework version (Avalonia 12 or 11, WinUI 3 unpackaged or packaged), a name, a folder and a title create a desktop
app from Core's embedded templates (no `dotnet new`; both Avalonia versions share `AvaloniaApp`, with the profile's
`TemplateVersion`; WinUI has `WinUIApp`): a csproj with an `Assets` item, `Program.cs` with an explicit `Main` (WinUI:
`DISABLE_XAML_GENERATED_MAIN`), `App` and `MainWindow` with code-behind, `app.manifest`, `global.json` and
`.gitignore`; a packaged WinUI app also gets `Package.appxmanifest` and generated logo PNGs. It is then built and its
main window opened.

### 11.5 New items
Add window / Add user control (and Add page in WinUI) create `{Name}.axaml` (`.xaml` in WinUI) and its code-behind in
the project's namespace from the framework's item templates (`Templates/Items`, `Templates/WinUIItems`) and open them.

### 11.6 File watching
Open `.axaml` files are watched. A change on disk that differs from what the designer last loaded or saved reloads
silently when the document is clean, and asks when it has unsaved changes. The build output is watched for builds made
elsewhere.

## 12. Images (`Core/Assets`)
Image properties get a picker: the project's images under `Assets/` with thumbnails, Browse…, and Remove. Browse copies
the file into `Assets/` (an identical file is reused; a different file with the same name gets a numbered name), adds
the framework's Assets item to the project if nothing covers Assets (a text edit that keeps the rest of the file byte
for byte), sets the property, and rebuilds without saving other files, so the renderer has the image. Avalonia:
`<AvaloniaResource Include="Assets\**" />` and `/Assets/{file}`. WinUI: `<Content Include="Assets\**"
Exclude="@(Content)" CopyToOutputDirectory="PreserveNewest" />` (the exclusion keeps files the project already lists,
such as Visual Studio's logos, from being packaged twice) and `ms-appx:///Assets/{file}`. Asset paths that would
resolve outside the project are rejected. Files outside a project get a message instead.

## 13. Clipboard (`Core/Editing/DesignerActions.Clipboard.cs`)
Copy writes the top-most selected elements, re-indented, inside an `<xd:Fragment>` with the namespace declarations they
need and the source document's id, as a custom clipboard format plus plain text. Paste accepts that or plain XAML,
finds a container from the selection, renames clashing names, strips event attributes when the fragment came from
another document, drops attached properties of other panel kinds and offsets positions. Cut is copy plus delete.

## 14. Render host (`XDesigner.RenderHost`)

### 14.0 One host per framework version
`XDesigner.RenderHost.Avalonia` is built twice: against Avalonia 12 (`XDesigner.RenderHost.Avalonia12.exe`) and, with
`AVALONIA11` defined for the few API differences, against Avalonia 11.3 (`XDesigner.RenderHost.Avalonia11.exe`);
Avalonia 11.1 and 11.2 projects run on the 11.3 host. `XDesigner.RenderHost.WinUI.exe` draws WinUI 3 (§14.5). What
doesn't depend on a framework is in `XDesigner.RenderHost.Common`: the command line, the pipe server and its exit
codes, the PNG writer and render-once output, the frame size limits (`FrameScale`) and C# type names for handlers.
§14.1–14.4 describe the Avalonia host; §14.5 how the WinUI host differs.

### 14.1 Startup
`XDesigner.RenderHost.Avalonia{11|12}.exe --pipe {name} --parent-pid {pid} [--assembly {project dll}] [--no-user-app]` (also
`--render {file} --out {png}` for one-off renders, and `--version`). It exits when the app process does. Design mode is
switched on before the project's App is created (`Design.IsDesignMode` has only an internal setter in Avalonia 12, so
it's set by reflection; the runtime loader's `DesignMode` flag doesn't set it). The project's assemblies load from
memory, with their dependencies resolved through the project's `deps.json`. The AppBuilder is the project's own
`BuildAvaloniaApp()` when it has one, else its App type, else a default Fluent app; then headless with Skia, without a
lifetime, so the project's startup code doesn't open windows. If the project's App fails to start, the app restarts the
host with `--no-user-app`.

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
Public, non-abstract, instantiable `Control` types from Avalonia and the project; content model from `[Content]` and
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
  files are hard-linked, the project's are copied, so rebuilding never meets a locked file. There, `ms-appx:///` URIs,
  compiled XAML (`.xbf`) and images resolve as in the user's app. The project's `App.xbf` is left out: WinUI would load
  it into whichever Application runs. A folder is held by an open lock file; each start removes the folders nobody holds.
- **Application.** `HostApp` (no App.xaml; started by `Program.Main` with `Application.Start`) implements
  `IXamlMetadataProvider` over the controls library's provider, its own generated one (found by reflection) and the
  project's, so `XamlReader` knows every type. It merges `XamlControlsResources` and the `<Application.Resources>` of the
  project's App.xaml, read from the project folder (`--project`). The project's App class never runs.
- **Design ids.** `DesignId` is an attached property on a class that isn't a DependencyObject (the XAML compiler would
  otherwise take its inherited GetValue/SetValue); `TypeInfoAnchor.xaml` makes the compiler generate its type info.
- **Rendering.** Work runs on the UI thread one piece at a time (`HostApp.InvokeTaskAsync`). The sanitizer turns a
  `Window` root into a Grid standing for its client area (a Window isn't an element) and removes what runtime XAML
  can't have: `x:Class`, event attributes, `{x:Bind}`, `x:Load`, `x:Phase`, `x:DataType`, and the `d:` namespace (after
  reading the design size). The root goes into a Viewbox sized at the requested scale (zoom is crisp; RasterizationScale
  doesn't change a RenderTargetBitmap's size) inside a Border with the theme's page background, on a canvas in a
  cloaked, never-activated tool window. After layout it waits for images to decode and reach the screen, then captures
  with `RenderTargetBitmap`. Layout comes from `TransformToVisual`; Grid offsets include Padding and Row/ColumnSpacing.
  A Window's root is reported as `Microsoft.UI.Xaml.Window` without panel info; Pages are reported as UserControls.
- **Metadata and values.** Types deriving from UIElement, plus Window; content models from `[ContentProperty]` and base
  types; dependency properties (a settable CLR property with a static `{Name}Property`), plain settable properties and
  attached properties (Grid, Canvas, RelativePanel, VariableSizedWrapGrid, ToolTipService, ScrollViewer); events with
  `RoutedEvent` fields marked routed. Values come from `ReadLocalValue`/`GetValue`, so the source is Local or Default
  only. Fonts are read from the registry (WinUI has no font list).

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
it open longer; decisions stay). View models: `ShellViewModel` (documents, project, build/run, settings, logs),
`DocumentViewModel` (document, undo, selection, preview state, render scheduling), `OutlineNodeViewModel`. Last-chance
handlers report unexpected exceptions in Output, a notification and `%LOCALAPPDATA%\xDesigner\errors.log` (capped at
512 KB) instead of closing the app with unsaved work. A named mutex (`xDesigner.Running`) lets the installer see that
the app is open.

### 15.2 Renderer management
`RenderHostManager`: one host per `HostKey` (framework profile and project assembly; "no project" is one more), the
profile's exe from `renderhosts\{HostFolder}\` (a WinUI host from a fresh shadow folder of the project's output, §14.5),
started on first use, restarted after a
crash at most three times a minute (then Retry), restarted after a build that changed the assembly. Requests interrupted
by a planned restart are sent once more to the new host. `RenderHostConnection` (Core) starts the process, connects,
handshakes, keeps recent stderr for diagnostics, and kills the process if a shutdown request doesn't end it.

### 15.3 Settings
`%LOCALAPPDATA%\xDesigner\settings.json` (`Core/Settings`): recent files and projects, the code editor (automatic, VS
Code, Visual Studio, Windows default or a custom command with `{file}` and `{line}`), the preview theme for new files,
snapping, "show advanced", and pane sizes. Unreadable settings fall back to defaults. File › Settings… edits them.
Help › Open the settings and error log folder opens the folder.

### 15.4 Keyboard
Listed in the README. Accelerators that would steal keys from text boxes (Ctrl+C/V/X/Z/Y, Delete) are handled at window
level only when no text box has focus.

## 16. Errors and robustness
| Failure | Handling |
|---|---|
| XAML or custom control error | Error banner, last good frame dimmed, Undo offered |
| Control hangs while drawing | Renderer stopped after 10 s, banner with Retry (§14.2) |
| Renderer crashes | Restarted on the next request (max 3 a minute), interrupted request retried |
| Project App fails to start in the renderer | Host restarted with the default theme, warning in Output |
| Build fails | Build output shown; the renderer keeps the previous assemblies |
| Code-behind missing or unparseable | Message; the XAML isn't changed |
| File can't be written | Message; the document stays dirty |
| File changed on disk | §11.6 |
| Unexpected exception | Reported (Output, notification, errors.log); the app keeps running |

## 17. Build and release
`global.json` pins the .NET 10 SDK. `Directory.Build.props` sets the version, product metadata, nullable, the
recommended analyzers and code-style enforcement, and treats warnings as errors in Release. The app is unpackaged and
self-contained (Windows App SDK included), x64 only. Release publishing (`dotnet publish src/XDesigner.App -c Release`)
is self-contained and ReadyToRun, and publishes each render host self-contained into `renderhosts\{folder}\`. The
installer has one component per render host, so users can leave out frameworks they don't design. To keep it lean, the
app and the WinUI host exclude the Windows App SDK's AI, ML, Search and Widgets parts, use the non-WPF build of
`Microsoft.VisualStudio.Threading` (its Windows build would pull in all of WPF; `build\VsThreadingWithoutWpf.targets`),
and nothing publishes `.pdb` files. The WinUI host is built and published for x64 (the app's `RenderHostProject` items
carry each host's platform).
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
- Tests marked `Category=Slow` run `dotnet build` or MSBuild and need the SDK and NuGet packages.
- The UI is checked by hand in the running app before a release: designing, events and Run on a new project; every
  panel and editor; preview; images; error and hang handling; external edits; install, upgrade and uninstall.
