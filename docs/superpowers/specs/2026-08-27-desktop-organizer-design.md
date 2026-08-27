# DesktopOrganizer — Design Spec

- **Date**: 2026-08-27
- **Status**: Draft
- **Scope**: v1 / MVP first, iterate after

## 1. Overview

A Windows 11 desktop icon organizer for personal use. Icons are **auto-classified**
by category and grouped into semi-transparent, named **fences** drawn on the desk
top. Icons are repositioned into each fence's grid by directly moving the real
desktop icon coordinates (Win32 `SysListView32`), so **files are never moved** and
other programs that reference desktop paths keep working.

### Goals (v1)

- Auto-classify desktop icons into categories (extension / shortcut target /
  name keyword / custom rules).
- Auto-create one fence per detected category, with icons grid-arranged inside.
- Fences are draggable and resizable on the desktop; icons follow live.
- Multi-monitor support.
- Remember layout across restarts; restore on startup.
- Optional auto-start with Windows.
- Manual one-click tidy + optional live sorting of new icons.
- Minimal but solid: undo is out of scope for v1, settings UI is functional.

### Non-goals (v1, iterate later)

- No file moving/renaming; fences are visual only.
- No cloud sync, themes, plugins, portal-folder mirroring, or password boxes.
- No Defender-level reliability guarantees; best-effort, personal tool.

## 2. Architecture

```
┌────────────────────────────────────────────────────────┐
│  WPF main window (Settings / Rules / Categories)        │
└───────────────┬────────────────────────────────────────┘
                │ commands (MVVM)
┌───────────────▼────────────────────────────────────────┐
│                 Services (app/DesktopOrganizer)         │
│  FenceManager     分区增删改/拖动/折叠/重启恢复          │
│  ClassifierEngine 分类(扩展名/.lnk目标/关键词/自定义规则) │
│  ConfigService    规则+布局 JSON 读写                    │
│  StartupService   注册表自启 + Explorer 重启 watcher     │
└───┬──────────────────┬───────────────────┬─────────────┘
    │ Win32 图标操控     │ 桌面叠加渲染        │ 系统集成
┌───▼───────────────┐ ┌──▼───────────────┐ ┌▼─────────────┐
│ IconPositionCtl   │ │ OverlayRenderer  │ │ StartupSvc    │
│ 定位 SysListView32 │ │ 每屏透明置顶WPF   │ │ HKCU Run      │
│ LVM 读写图标坐标    │ │ 绘制半透明分区框  │ │ Explorer watcher
│ 分区网格计算/应用    │ │ click-through     │ │              │
└───────────────────┘ └──────────────────┘ └───────────────┘
          │                                   (app project)
          ▼
   Core/ (pure, unit-tested)
   Classifier + Rules + ConfigData types + Fence/Layout models
```

**Layer rule**: testable logic (classification, rule matching, data models,
serialization) lives in `DesktopOrganizer.Core`. Everything that touches Win32,
the desktop overlay, or WPF lives in the app project. P/Invoke is confined to
`Win32/`.

## 3. Components

### 3.1 Classification — Core

`ClassifierEngine` classifies each desktop item to a category.

Classification order (first match wins, manual override wins over all):

1. **Manual override** — user pinned a category for this icon (highest).
2. **Custom rules** — user-defined rules (filter: extension/name-keyword/target-app; action: category).
3. **Shortcut target app** — for `.lnk`, resolve target exe name → app category (browser/office/dev/game…).
4. **Extension** — `.png/.jpg` → Images, `.pdf/.docx` → Documents, `.mp4/.mkv` → Videos, `.zip/.rar` → Archives, `.mp3/.flac` → Audio, etc.
5. **Name keyword** — fallback关键词匹配.
6. **Default** → "Other".

Categories + rules are loaded from config; extensible via the rules editor in
the settings window.

### 3.2 FenceManager — app

Owns the list of `Fence` instances. Each fence has:

- `Id`, `Title`, `Category`
- monitor/screen bounds it lives on
- `Rect` (desktop coordinates), title bar, color, opacity
- `IconIds` (desktop icons currently in this fence)
- `Collapsed` state
- grid layout params (cell size, spacing, max columns)

Operations: create/rename/delete, drag (`MoveBy`), resize (`ResizeTo`),
collapse/expand, add/remove icons, recompute grid.

### 3.3 IconPositionController — app, Win32

- Find the desktop window: `Progman` → `WorkerW` → `SHELLDLL_DefView` → `SysListView32` (per monitor/work area).
- Enumerate items (icon name + index) via `LVM_GETITEMCOUNT` / `LVM_GETITEMW` / `LVM_GETITEMTEXTW`.
- Get/set positions with `LVM_GETITEMPOSITION` / `LVM_SETITEMPOSITION(EX)`.
- Compute a fence's icon grid (cell size → columns/per-monitor → positions) and apply to the target icons.
- Track the item that Explorer maps to each icon path/name to keep mapping stable.

### 3.4 OverlayRenderer — app

- One layered, topmost, transparent WPF window per monitor, positioned over the
  desktop (bound behind icons via the `Progman/WorkerW` technique where feasible).
- Renders each fence: rounded semi-transparent background, title bar, drag
  handles. Hit-testing is click-through (`WS_EX_TRANSPARENT`) except on the
  fence chrome (title bar / resize grips) so real icons underneath stay clickable.
- Drag/resize messages come from the fence chrome; FenceManager recomputes the
  grid and IconPositionController re-applies it.

### 3.5 ConfigService — Core (data) / app (IO)

- **Data types** live in Core (serializable, unit-tested): `FenceLayout`,
  `CategoryRule`, `ClassifierConfig`, `ManualOverride`.
- File: `%LocalAppData%\DesktopOrganizer\config.json` (+ a `.bak`). Write on
  change (debounced), load on startup.

### 3.6 StartupService — app

- Enable/disable auto-start via `HKCU\...\Run` (`DesktopOrganizer` value).
- Watch for Explorer restart (poll `Progman` HWND / `WM_` «Explorer-exit»
  events) and re-attach overlay + re-locate `SysListView32`.

## 4. Data model (Core)

```csharp
enum Category { Other, Images, Documents, Videos, Audio, Archives,
                Applications, Browser, Office, Dev, Games, Downloads, ... }

sealed record IconEntry(int Index, string Name, string Path, string? LinkTargetApp, Category Category = Category.Other);

sealed record Fence { string Id; string Title; Category Category;
                      string MonitorId; RectI Rect; bool Collapsed;
                      ColorArgb Fill; double Opacity; int MaxColumns;
                      List<string> IconNames; }

sealed record RectI(int X, int Y, int Width, int Height);

sealed class CategoryRule { string Id; Category? Category;
                             bool MatchAny; List<RulePredicate> Predicates; }
sealed record RulePredicate(RuleField Field, RuleOp Op, string Value);
enum RuleField { NameKeyword, Extension, LinkTargetApp }
enum RuleOp { Equals, Contains, StartsWith, Matches (regex) }

sealed class ClassifierConfig { string Version; List<CategoryRule> Rules; Dictionary<string,Category> Overrides;
                                // Overrides uses StringComparer.OrdinalIgnoreCase (preserved by ConfigSerializer on load) }

sealed record AppSettings { FenceLayout? Layout; ClassifierConfig Classifier; bool AutoStart; bool LiveSort; }
```

## 5. Persistence & lifecycle

- Startup: load `config.json` → restore fences → re-locate desktop window →
  render overlay → apply restored layout (icons into fences).
- Changes (drag, resize, recategorize, rule edits) → debounced save to JSON +
  `.bak`.
- Display/DPI change: hook `WM_DISPLAYCHANGE` → recompute grid coordinates.
- Explorer restart: watcher re-attaches; no crash.

## 6. Error handling

- Explorer/desktop window not found (e.g. shell not loaded): retry on a small
  backoff; keep app alive in tray.
- Icon resolve failures: skip icon gracefully, log, never move files (we never
  move files).
- Config corruption: back up corrupt file, fall back to defaults, log.
- All failures logged via Serilog; no silent data loss (we only reposition
  icons that the OS already knows).

## 7. Testing

- **Unit (Core, xUnit)**: classifier precedence & overrides; rule matching
  (all fields/ops); category-from-extension map; config round-trip
  serialize/deserialize; default-rules correctness.
- **Manual (app)**: overlay rendering, drag/resize of fences, multi-monitor,
  Explorer restart recovery, startup layout restore. Win32 interplay is
  interactively verified on the real desktop.

## 8. Milestones (MVP → iterate)

1. **M1 Core plumbing**: models, classifier, rules, config round-trip + tests.
2. **M2 Desktop hook**: locate desktop `SysListView32`, read icons, write
   positions; proof-of-concept grid layout applied.
3. **M3 Overlay + fences**: render fences, drag/resize, icons follow.
4. **M4 Auto-classify + auto-create fences**: one-click tidy.
5. **M5 Persistence + startup**: restore layout, auto-start, live sort option.
6. **M6 Settings UI**: rules editor, category manager, fence style.

Each milestone lands on `main` with small conventional commits.