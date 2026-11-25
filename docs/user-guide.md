# xDesigner user guide

xDesigner designs the windows, pages and user controls of [Avalonia UI](https://avaloniaui.net/) 11 and 12, WinUI 3 and
.NET MAUI 10 and 11 apps the way the WinForms designer does: you place controls with the mouse, set their properties in
a properties window and write the event handlers in C#. It writes the XAML files (`.axaml`, `.xaml`) for you.

- [The window](#the-window)
- [Projects and files](#projects-and-files)
- [Designing a form](#designing-a-form)
- [Properties](#properties)
- [Events and code](#events-and-code)
- [Items, tabs, menus and grids](#items-tabs-menus-and-grids)
- [The outline](#the-outline)
- [Images](#images)
- [Previewing](#previewing)
- [Building and running](#building-and-running)
- [Settings](#settings)
- [When something goes wrong](#when-something-goes-wrong)

## The window

| Area | What it's for |
|---|---|
| Title bar | The menus (File, Edit, View, Project, Help) and a toolbar: Open, Run/Stop, and the preview's Theme, Size and Zoom |
| Toolbox (left) | Controls to drag onto the form. Search at the top; your project's own controls are under **Project** |
| Design surface (middle) | One tab per open file |
| Solution / Outline (right, top) | **Solution**: the open solution's projects and files (Solution Explorer); **Outline**: every control of the file as a tree |
| Properties / Events (right, bottom) | The selected control's properties and events |
| Output (bottom) | Messages from xDesigner (**Output**), the build (**Build**), your running app (**Run**), every handler created (**Events**) and the last build's errors and warnings (**Error List**) |
| Status bar | Renderer state, file path, the selection's position and size, zoom, grid snapping |

Panes can be resized by dragging the bars between them; xDesigner remembers their sizes. **View ▸ Toolbox** and
**View ▸ Output** hide or show those panes; **View ▸ Solution Explorer** (Ctrl+Alt+L) and **View ▸ Error List** bring
those forward. Messages that need your
attention (confirmations, warnings, errors) slide in under the title bar.

## Projects and files

- **File ▸ New project…** (Ctrl+Shift+N) creates a desktop app: choose Avalonia 12, Avalonia 11, WinUI 3 or .NET MAUI
  11 or 10 (and for WinUI and MAUI whether it is **Packaged (MSIX)**), a name, a folder (by default
  `Documents\xDesigner Projects`) and the window title (a MAUI app's main page title). A MAUI app is created for Windows; add Android, iOS or macOS to its
  project file later. xDesigner builds it and opens its main window. The project is a normal .NET project; open it in Visual Studio,
  VS Code or Rider whenever you like.
- **Project ▸ Open project…** (Ctrl+Shift+O) opens a `.csproj`, `.sln` or `.slnx`. All of a solution's projects open
  together, in **Solution Explorer** (the **Solution** tab above the outline): each project shows the framework it
  uses, and the startup project (the one Run starts, in bold) can be changed with a right-click. Projects can use
  Avalonia 11 (11.1 or later), Avalonia 12, WinUI 3 or .NET MAUI 10 or 11, mixed in one solution: each file is drawn by
  its own project's framework. Double-click a XAML file to design it, or any other file to open it in your code editor. The startup
  project's XAML files are also listed under **Project ▸ Files**.
- **File ▸ Open…** (Ctrl+O) opens a single XAML file (`.axaml`, `.xaml`), or a project or solution. A file opened on its own still finds its
  project, so its custom controls render.
- **Project ▸ Add window…** and **Add user control…** (and **Add page…** in a WinUI project) create a new `.axaml`
  file (`.xaml` in WinUI) with its code-behind and open it. In a MAUI project they are **Add content page…** and **Add
  content view…** (MAUI windows are made in code).
- **File ▸ Open recent** lists recent files and projects.
- **Save** (Ctrl+S), **Save all** (Ctrl+Shift+S), **Close** (Ctrl+W). A tab with unsaved changes shows a dot, and
  xDesigner asks before closing it.

If a file changes on disk while it's open (you edited it in another editor, or switched Git branches), xDesigner
reloads it. If you also have unsaved changes, it asks which version to keep.

## Designing a form

**Adding controls.** Drag a control from the toolbox onto the form, or double-click it in the toolbox to add it to the
selected container. Where it lands depends on the container under the pointer:

| Container | Dropping a control |
|---|---|
| Grid | puts it in the cell under the pointer (the grid lines show while you drag) |
| Canvas | puts it at the pointer |
| StackPanel, WrapPanel | inserts it between the controls already there (a line shows where) |
| DockPanel | docks it to the nearest edge, or fills the middle |
| Button, Border, ScrollViewer, … | makes it the content, if there's none yet |

New controls are named `button1`, `textBox2` and so on; you can use those names in your code straight away.

**Selecting.** Click a control to select it. Ctrl+click adds or removes a control, Shift+click adds, and dragging on
an empty area of a panel draws a selection rectangle. Esc selects the parent, Tab the next control in the same
container, Ctrl+A all controls in the same container. You can also select in the outline.

**Moving and resizing.** Drag a selected control to move it, inside its container or into another one. Drag a handle
to resize it. Snap lines appear when an edge or centre lines up with another control, or when the gap between them is
8 pixels. Hold **Alt** while dragging to keep the control in its current container and turn snapping off. The arrow
keys move the selection by one pixel (Shift: ten); in a StackPanel they move it up or down the list instead.
Grid snapping is switched on from the status bar.

**Editing.** Cut, copy, paste and delete work as usual, also between files. Pasted controls are renamed if their names
are taken. Undo (Ctrl+Z) and redo (Ctrl+Y) cover every change. Right-click a control for more: select its parent,
bring it to the front or send it to the back, **Wrap in** a new panel of the file's framework (Grid, StackPanel, Border,
Canvas, DockPanel, WrapPanel or ScrollViewer; in MAUI Grid, Border, VerticalStackLayout, HorizontalStackLayout,
AbsoluteLayout, FlexLayout or ScrollView), or open its item or row/column editor. Controls can't be pasted between
files of different frameworks.

## Properties

The Properties tab shows the selected control's name (click it to rename; F4 jumps there) and its type, then its
properties by category. Use the search box to find one, **A–Z** to sort by name, and the filters to show only the
properties you've set or also the advanced ones.

- Properties you've set are **bold** and have a reset button, which removes them from the file.
- Others show the value the control actually has, greyed (for example one that comes from the theme).
- Each kind of value has its own editor: text (the **…** button opens a multi-line editor), numbers (Up/Down change
  them), check boxes, lists, colors, fonts, margins and padding (one value, or each side), corner radii, Grid sizes
  and images.
- Values that are bindings or resources (`{Binding …}`, `{StaticResource …}`) are shown but not edited.
- With several controls selected, the properties they share are shown, and a change applies to all of them.

Changes apply when you press Enter or leave the box; an invalid value is refused with a message.

## Events and code

Double-click a control on the form to create the handler of its main event (Click for a Button, TextChanged for a
TextBox, and so on). xDesigner:

1. gives the control a name, if it doesn't have one,
2. adds an empty method such as `button1_Click` to the code-behind file (`MainWindow.axaml.cs`),
3. connects it in the XAML, and
4. tells you the method, file and line, with **Open in editor** and **Copy method name** buttons.

The **Events** tab lists every event of the selected control. Type a method name and press Enter to connect it (the
method is created if it doesn't exist), pick an existing method from the list, or press the button to create or jump
to the default handler. Clearing the box disconnects the event.

xDesigner never changes code you wrote: undoing a handler, or renaming or deleting the control, only changes the
XAML. The method stays in your code-behind for you to delete or reuse.

## Items, tabs, menus and grids

**Items.** For a ListBox, ComboBox, TabControl, Menu or TreeView, right-click it and choose **Edit items…** (**Edit
tabs…**, **Edit menu items…**, **Edit nodes…**). Add, remove, reorder and rename the items; Menu and TreeView items can have sub-items.
A new tab gets an empty Grid to design in. To design a tab, select it in the editor or the outline: the form shows it.

**Grid rows and columns.** Right-click a Grid and choose **Edit rows and columns…**. Each row or column is **Auto**
(fits its content), **\*** (shares the remaining space; `2*` takes twice as much) or a fixed size in pixels, with an
optional minimum and maximum. Controls move with their row or column, and the controls of a deleted one move to the
row or column before it.

## The outline

The outline shows every control of the file. Selecting there selects on the form and the other way round, which is
the easiest way to reach a control that's hidden behind another one or has no size. Drag a row to move the control:
drop it on the top or bottom of another row to put it before or after, or on the middle to put it inside. The
right-click menu is the same as on the form.

The **eye** icon hides a control on the form while you design, to get at what's under it. It doesn't change the file;
**View ▸ Show hidden controls** brings them all back.

## Images

Properties that take an image (an Image's Source, a Window's Icon) have a picker that lists the images in the
project's `Assets` folder. **Browse…** picks any image: xDesigner copies it into `Assets` (keeping a numbered copy if a
different file of that name is there), makes sure the project includes `Assets` (as Avalonia resources, or in WinUI as
content copied next to the app), sets the property to `/Assets/name.png` (`ms-appx:///Assets/name.png` in WinUI) and
rebuilds so the form can show it. In a MAUI project images go to `Resources\Images` as MauiImages, renamed to the
lowercase letters, digits and underscores MAUI allows, and are set by file name (`my_logo.png`). **Remove** clears the property. Images need a project; for a
file opened on its own, xDesigner explains that.

## Previewing

The toolbar in the title bar sets how the open form is previewed:

- **Theme**: Default (the app's own), Light or Dark.
- **Size**: the form's design size, a few common window sizes, or **Custom…**. This only changes the preview; **View ▸
  Keep this preview size** writes it into the file (as an Avalonia Window's Width and Height, otherwise as the design size,
  `d:DesignWidth` and `d:DesignHeight`).
- **Zoom**: 25 % to 400 %, or Fit. Ctrl+mouse wheel, Ctrl+= and Ctrl+- zoom; Ctrl+0 is 100 %; Ctrl+9 fits the form in
  the window.

None of these change the file, except Keep this preview size.

## Building and running

xDesigner builds the project when you open it, after **Project ▸ Rebuild** (Ctrl+Shift+B) and before running. Custom
controls and images appear on the form after a successful build. Builds made outside xDesigner, for example in Visual
Studio, are picked up too, for every project of the solution (a rebuilt library reloads the projects that use it). A
build can be cancelled with **Project ▸ Cancel build**, and when one project of a solution fails, the others still get
their new build.

**Run** (F5) saves your changes, builds and starts the app; its console output goes to Output ▸ Run. **Stop**
(Shift+F5) closes it. Build errors are listed in Output ▸ Error List (double-click one to open its file at the line; a XAML file opens in the designer with the control
at that line selected)
and, in full, in Output ▸ Build. With a solution open, Rebuild builds the whole solution and Run starts the startup
project.

A packaged WinUI or MAUI app is run the way Visual Studio runs it: xDesigner registers the build with Windows and starts
it as an installed app (its console output isn't shown). Windows only allows that with **Developer Mode** on (Settings ▸
System ▸ For developers). Without it, an app that carries the Windows App SDK itself (`WindowsAppSDKSelfContained`) is
started from its exe, and xDesigner says so in Output ▸ Run; it then works, except for features that need a package
identity, such as notifications. One that uses the Windows App SDK installed on Windows can only run as a package, so
xDesigner asks you to turn on Developer Mode.

Building and running needs the [.NET SDK](https://dotnet.microsoft.com/download) the project uses (its `global.json` can
ask for a version), and for a .NET MAUI project the MAUI workload for Windows (`dotnet workload install maui-windows`, in
an administrator terminal). xDesigner builds and runs MAUI projects for Windows only, so the Android and iOS workloads
aren't needed. Opening and designing files works without any of them.

## Settings

**File ▸ Settings…** (Ctrl+,):

- **Code editor**: where Open in editor opens code. Automatic uses VS Code if it's installed, otherwise the program
  Windows opens `.cs` files with. You can also choose Visual Studio, or a custom command where `{file}` and `{line}`
  are filled in, for example `rider64.exe --line {line} {file}`.
- **Preview**: the theme new files open with.
- **Snapping**: snapping to other controls, snapping to a grid, and the grid size.
- **Properties window**: show advanced properties and events by default.

Settings are stored in `%LOCALAPPDATA%\xDesigner\settings.json`; **Help ▸ Open the settings and error log folder**
opens it.

## When something goes wrong

- **The form shows a red banner.** The file can't be shown as it is: often a custom control that throws, or a value
  Avalonia can't load. The banner gives the error and the line, and the last good picture stays dimmed underneath.
  Press **Undo** to take back the last change, or fix the control in your code and rebuild.
- **"The renderer was stopped".** Drawing the form took more than 10 seconds, usually because a custom control is
  stuck in a loop. Fix the control, rebuild, and press **Retry**.
- **Custom controls don't appear.** Build the project (Project ▸ Rebuild) and check Output ▸ Build for errors.
- **Something unexpected happened.** xDesigner reports it in Output and keeps running, so you can save your work. The
  details are in `errors.log` in the settings folder (Help ▸ Open the settings and error log folder). Please
  [report it](https://github.com/CGrebill/xDesigner/issues) (Help ▸ Report a problem), with that file if you can.
