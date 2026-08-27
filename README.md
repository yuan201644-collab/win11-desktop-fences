# DesktopOrganizer

A Windows 11 desktop icon organizer. Auto-classifies desktop icons and pins them
into semi-transparent, freely-draggable **fences** so your desktop stays clean —
without moving the files themselves.

Built for personal use on Windows 11. C# / .NET 9 / WPF.

## Highlights

- **Auto-classify** icons by file extension, the app a shortcut points to, and
  name/keyword rules (configurable).
- **Fences** — semi-transparent, named zones drawn on the desktop layer. Real
  icons are repositioned into each fence's grid via the Win32 `SysListView32`,
  so no files are ever moved and other apps keep working with desktop paths.
- **Free drag** — move/resize a fence anywhere on the desktop; icons follow.
- **Multi-monitor** aware.
- **Remembers layout** across restarts (JSON) and can **auto-start** with Windows.
- **Trigger modes**: manual one-click tidy, or optional live sorting of new icons.

> Personal, non-commercial project.

## Tech stack

| Layer        | Choice                                                    |
|--------------|-----------------------------------------------------------|
| Runtime      | .NET 9 (net9.0 / net9.0-windows)                          |
| UI           | WPF + CommunityToolkit.Mvvm                               |
| DI           | Microsoft.Extensions.Hosting                              |
| Logging      | Serilog (rolling file)                                    |
| Icon control | P/Invoke Win32 (`SysListView32`, `LVM_*`, `WorkerW`, …)   |
| Tests        | xUnit (`DesktopOrganizer.Core`)                           |

## Repo layout

```
src/
  DesktopOrganizer/          # WPF app (MVVM)
    Views/  ViewModels/  Models/  Services/  Win32/
  DesktopOrganizer.Core/     # pure logic: classifier, rules, config
  DesktopOrganizer.Tests/    # xUnit tests for Core
docs/
  superpowers/specs/         # design docs
```

## Build & run

```bash
dotnet build
dotnet run --project src/DesktopOrganizer
```

Publish a single self-contained exe:

```bash
dotnet publish src/DesktopOrganizer -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

## Tests

```bash
dotnet test
```

## License

MIT (see LICENSE).