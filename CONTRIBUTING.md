# Contributing to xDesigner

Thanks for your interest! Bug reports, ideas and pull requests are welcome.

## Before you start

- xDesigner is licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE.md). By contributing you agree that
  your contribution is licensed to the project under the same terms, and that the author may also license it on
  other terms (for example to allow commercial use).
- For anything bigger than a small fix, please open an issue first to discuss it.

## Building and testing

You need Windows 10 2004+ (or Windows 11) and the .NET 10 SDK. `global.json` selects the SDK.

```powershell
dotnet build xDesigner.slnx
dotnet test xDesigner.slnx                               # all tests
dotnet test xDesigner.slnx --filter "Category!=Slow"     # skip the tests that run dotnet build / MSBuild
dotnet run --project src/XDesigner.App                   # start the designer
```

The release installer is made by `build\publish.ps1` (needs [Inno Setup 6](https://jrsoftware.org/isinfo.php)).

## Layout

| Path | What it is |
|---|---|
| `src/XDesigner.App` | The WinUI 3 designer (unpackaged exe): shell, design surface, panels, dialogs |
| `src/XDesigner.Core` | Editing logic without any UI: XAML document model, undo, placement, snapping, code-behind writer, projects |
| `src/XDesigner.Protocol` | The RPC contract and data types shared by the app and the render host |
| `src/XDesigner.RenderHost.Avalonia` | The out-of-process Avalonia headless renderer: its source, and the project that builds it against Avalonia 12 |
| `src/XDesigner.RenderHost.Avalonia11` | Builds the same renderer source against Avalonia 11 (`AVALONIA11`) |
| `tests/` | xUnit v3 tests for Core and for the render hosts (they start the real renderer); the Avalonia 11 host's tests share the Avalonia 12 host's sources |
| `samples/SampleAvaloniaApp`, `samples/SampleAvalonia11App` | The same app with custom controls on Avalonia 12 and 11, used by the tests |
| `installer/`, `build/` | Inno Setup script and the release build script |
| `tools/` | The icon generator and the third-party notices generator |
| `docs/` | User guide and architecture |

[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) explains how the pieces work together; code comments refer to its
sections as `§n.n`.

## Conventions

- `dotnet build -c Release` must pass with zero warnings (warnings are errors in Release; the recommended .NET
  analyzers and `.editorconfig` style rules are on).
- Keep logic in `XDesigner.Core` (testable without UI) and add tests for it. The App stays thin.
- Every edit to a document is an undoable command, and undo must restore the file byte for byte.
- Comments explain why, in plain English. Write user-facing messages so they say what happened and what to do next.
- After updating packages, regenerate `THIRD-PARTY-NOTICES.md` with `tools\make-notices.ps1`.
