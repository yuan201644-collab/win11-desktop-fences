# CLAUDE.md — DesktopOrganizer

Handoff manual for Claude Code sessions working on this repo. Read before
changing anything.

## Project

Windows 11 desktop icon organizer (personal use). Auto-classifies desktop icons
into semi-transparent, freely-draggable **fences**. Icons are repositioned on
the desktop via Win32, files are **never** moved.

## Conventions

- **Branching**: single `main` branch, small incremental commits.
- **Commits**: English, [Conventional Commits]
  (`feat:` `fix:` `refactor:` `docs:` `chore:` `test:`).
- **Target**: .NET 9 (`net9.0`, WPF = `net9.0-windows`). Only the 9.0 SDK is
  installed on this machine.
- **CI/UI**: zero build warnings treated as errors (`TreatWarningsAsErrors`).
- **Dependencies**: `CommunityToolkit.Mvvm`, `Microsoft.Extensions.Hosting`,
  `Serilog`, `xUnit`.

## Repo layout

```
src/
  DesktopOrganizer/          # WPF app (MVVM)
    Views/       # XAML views
    ViewModels/  # VM layer (CommunityToolkit.Mvvm)
    Models/      # domain models
    Services/    # classifier, config, startup, fence manager
    Win32/       # ALL P/Invoke lives here, wrapped in typed methods
  DesktopOrganizer.Core/     # pure logic, minimal deps, unit-tested
  DesktopOrganizer.Tests/    # xUnit, targets Core
docs/
  superpowers/specs/         # design docs
```

## Architecture rule

UI (WPF) and the desktop-overlay/icon-position fiddling live in the app
project. Testable pure logic (classification, rule matching, config
serialization) must live in **Core** and have unit tests. P/Invoke is confined
to `Win32/`, never raw `DllImport` scattered in business code.

## Commands

```bash
dotnet build                       # build all
dotnet test                        # run tests
dotnet run --project src/DesktopOrganizer
dotnet publish src/DesktopOrganizer -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -o publish
```

## Design docs

Current spec: `docs/superpowers/specs/<date>-desktop-organizer-design.md`
(see file list for the latest date). Read it when implementing features.

## GitHub

Public repo. Remote `origin`. Default branch `main`. Push with
`git push origin main`.

[Conventional Commits]: https://www.conventionalcommits.org/