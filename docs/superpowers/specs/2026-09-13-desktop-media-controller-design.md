# Desktop Media Controller — Design Spec

- **Date**: 2026-09-13
- **Status**: Proposed (not started — no code written yet)
- **Scope**: a **standalone** desktop media widget + one integration hook into the icon packer
- **Origin**: derived from a Wallpaper Engine wallpaper mod ("音域回响"). The wallpaper route
  was abandoned after research showed WE web wallpapers have a **receive-only** media API
  (no way to send play/pause/next). See §4.

## 1. Overview

A small always-on-desktop window that shows what is currently playing (cover art, title,
artist, progress, scrolling lyrics) and lets the user control playback
(play/pause, previous, next, seek) **without switching windows**.

It is a **separate process**, not a new tab in the existing settings window. The reason is
structural, not cosmetic: a `MainWindow` tab is only reachable when the settings window is
open, which is exactly when you are *not* listening to music.

The one thing that does couple it to DesktopOrganizer is **placement**: while the controller
is on screen, the icon packer must treat its rectangle as an **uncoverable zone**. That
mechanism already exists in this repo (§6) — it needs an input, not an invention.

## 2. Goals

| # | Goal | Notes |
|---|---|---|
| G1 | Full now-playing display | cover, title, artist, elapsed/total, progress bar |
| G2 | Synced scrolling lyrics | current line highlighted, ±1 line visible, 3-line window |
| G3 | Playback control | play/pause, previous, next, seek (click/drag progress bar) |
| G4 | Freely movable | user drags it anywhere on any monitor; position persists |
| G5 | Always visible while open | stays on top; no auto-hide required |
| G6 | User-controllable lifecycle | user opens/closes it; **closed == offline** (see §6) |
| G7 | Never a nuisance | sees-through clicks must not block the desktop; must not swallow icon drags |
| G8 | Icon-packer integration | while online, its rect becomes a packer obstacle; when offline, packing is unaffected |
| G9 | DPI correct | PerMonitorV2, crisply placed on both monitors (1920@100% / 2560@125%) |

## 3. Non-goals

- Not a music library / playlist manager / search UI.
- Not a volume mixer (see open question Q3).
- No file movement; no changes to the wallpaper project (the wallpaper lyrics module is
  **retired** once G2 lands here — see §7 phase 3).
- No plugin/dependency injection framework; keep it a small tool.

## 4. Why this cannot be done inside the wallpaper (research result)

Verified against the official docs and developer statements:

- Wallpaper Engine web wallpapers get exactly five media listeners, **all inbound**:
  Status / Properties / Thumbnail / Playback / Timeline.
  There is **no** play/pause/next/previous/seek outbound call. This is architectural, not a
  missing lookup.
- Trap: `wallpaperPropertyListener.setPaused()` sounds like "pause the music" but actually
  means **"WE is pausing/restoring the wallpaper itself"**. Unrelated to media playback.
  Likewise `-control pause|play|mute` on the WE command line controls WE, not the system
  media session.
- The existing wallpaper already consumes the full inbound path (Properties / Thumbnail /
  Playback / Timeline are all registered; title, artist, thumbnail, `isPlaying`, `position`,
  `duration` are all available). So the *display* half is basically free — only the
  *outbound* half is missing.

**Conclusion**: control requires leaving the wallpaper and talking to the OS media session
directly.

## 5. Technical foundation

### 5.1 Control & state: Windows SMTC

The only sane system-wide route is **SMTC** (System Media Transport Controls):

```
Media Controller  ──►  Windows SMTC  ──►  QQ音乐 / 网易云 / Spotify / ...
```

- `GlobalSystemMediaTransportControlsSessionManager` gives per-app sessions, and each
  session exposes `TryPlayAsync / TryPauseAsync / TrySkipNextAsync / TrySkipPreviousAsync /
  TryChangePlaybackPositionAsync`, plus metadata (title/artist/thumbnail) and a timeline
  (position/duration). **This covers G1–G3 in one API.**
- This machine was probed (read-only): Windows 11 Build 26200, the WinRT SMTC type resolves
  fine, and QQ音乐 is installed. So the platform side is ready.

### 5.2 HARD BLOCKER: target framework

This repo targets **`net9.0-windows`** (`src/DesktopOrganizer/DesktopOrganizer.csproj`), and
per `CLAUDE.md` only the .NET 9 SDK is installed.

**SMTC is a WinRT API. `net9.0-windows` cannot see it.** Options:

| Option | What it costs | Verdict |
|---|---|---|
| A. Bump TFM to `net9.0-windows10.0.19041.0` | Touches the **existing app project**; pulls in the Windows SDK projection; may need `WindowsSdkPackageVersion` pinning; risks the current zero-warning build | Avoid for the main app |
| B. Give the **new project** its own TFM | Isolated; main app untouched | **Preferred — and it is a strong argument for a separate project rather than a new tab** |
| C. `SendInput` with `VK_MEDIA_*` keys | Zero TFM change, zero deps — but **fire-and-forget**: no state, no metadata, no progress | Fallback only; cannot satisfy G1 |

So: **G1 (display) forces SMTC, which forces TFM `...windows10.0.19041.0` somewhere.** Option
B keeps that blast radius inside the new project. Whether the new project lives in this
solution or its own repo is open question Q1.

> Verify before implementing: that `dotnet build` succeeds on `net9.0-windows10.0.19041.0`
> with only the .NET 9 SDK present (the Windows SDK projection usually restores from NuGet,
> but confirm rather than assume).

### 5.3 Library

Do not hand-roll the WinRT async interop. FluentFlyout (the closest mature peer) uses a
community SMTC wrapper. Candidate: the `WindowsMediaController` family of NuGet packages
(author Dubya). **Confirm the exact package id + version at implementation time** — do not
trust this line alone.

If a wrapper is used, still keep the calls behind a thin interface (e.g. `IMediaSession`) so
the widget can be unit-tested against a fake and so a future library swap is local.

### 5.4 PREREQUISITE, NOT YET VERIFIED: QQ音乐 SMTC support

Earlier public info claiming "QQ音乐 does not support SMTC" is **outdated**. As of roughly
mid-2024 QQ音乐 ships SMTC support, but it is **off by default**:

- version **≥ 21.10.2962**, and
- setting **设置 → 通用设置 → 通知 → 显示系统媒体传输控制（SMTC）** turned **on**.

**The earlier probe found zero media sessions — most likely this switch, not a missing
feature.** 网易云 / 酷狗 / 酷我 have analogous opt-ins.

> This is phase-0 work: confirm the version and flip the switch, then re-probe the session
> list. **Do not start UI work on top of an unverified media source.**

### 5.5 Window technique — reuse what this repo already proved

| Need | Existing precedent in this repo | Action |
|---|---|---|
| Independent top-level window, never reparented | `UI/FenceWindow.cs:181-183` — **"never reparented into the shell. Reparenting a WPF AllowsTransparency window via `SetParent` hangs the UI thread in a shell handshake"** | **Never `SetParent` the controller.** Top-level only. |
| Transparent window that lets clicks through / doesn't steal focus | `Win32/OverlayNative.ApplyFenceStyles` + `WM_NCHITTEST` hit-testing | Copy the style approach; but the controller **must** accept clicks on its own buttons, so it needs its own hit-test map rather than blanket click-through |
| PerMonitorV2 px→DIP placement | `UI/SnapPreviewWindow.cs`, `FenceWindow` — `TransformToDevice.M11`, re-queried per render, **never cached** | Copy verbatim |
| Tray presence, hide-on-close | `App.xaml` + `AutoStartService` | Reuse the pattern |
| P/Invoke placement | `Win32/` is the only home for `DllImport`; 28 call sites, all compliant | Enforce for the controller too |

Note the existing **DPI reality** on this machine: virtual screen `(-2560,0) 4480×1600`, main
1920×1080 @100%, secondary 2560×1600 @125%. The controller must be placed and dragged
correctly across that mix, and **persist physical pixel coordinates** so a DPI change doesn't
drift it.

### 5.6 Lyrics (G2)

Port the logic already proven in the wallpaper module rather than starting over. The
wallpaper's approach reached **87% synced / 92% any-lyrics coverage on 100 real 2026 chart
songs**; the raw source is LRCLIB (free, CORS-open, no key).

The three parts that actually made it work — and the exact reasons the naive version failed:

1. **Search by title, never put the artist in the query.**
   LRCLIB stores Romanized/English artist names (孙燕姿→Stefanie Sun, 李荣浩→Li Ronghao,
   薛之谦→Joker Xue). WE and the SMTC feed hand over Chinese names. `/api/get` demands an
   **exact** artist match → 404; and `/api/search?q=<title> <artist>` returns **0 rows**.
   Query by `track_name` (or bare `?q=`) only, then rank locally.
2. **Local ranking**: prefer synced over plain, then closest duration, then title exactness,
   then artist relatedness.
3. **Qualification gate + fuzzy title match**: prevents same-title-different-artist
   mis-lyric (李荣浩《恋人》→ 福山雅治《恋人》). Accept when artist is related **or**
   (title matches and `|Δduration| ≤ 8s`). Title comparison via normalized edit distance
   (≥0.75 similarity, skip titles < 4 chars) to tolerate 繁简 and `(Live)` suffixes.
4. **Retry transient 503/502/504** — an 11-in-100 occurrence rate; without retry those songs
   are silently lost.

Residual misses (~8%) were genuinely absent from LRCLIB, not a bug.

## 6. Integration with DesktopOrganizer (G8) — the interesting part

### 6.1 The mechanism already exists

`Services/DesktopLayoutService.cs`:

- `PackRowMajor(items, fence, obstacles)` — **L248**. Signature already takes
  `IReadOnlyList<RectI>? obstacles`.
- Production call site **L216** already passes the rectangles of **pinned** fences as
  obstacles, after laying those out first (L188-211). `L116` passes `null` (the single-fence
  re-arrange path).
- `OverlapsAny(box, obstacles)` — **L338**. Edge-touching does not count as overlap.

So "controllers become no-go zones" is **one more rectangle in an existing list** — exactly
the same way pinned fences already reserve space. No new algorithm.

### 6.2 Online detection: no IPC needed

**Presence of a *visible* window == online.** Enumerate top-level windows, match the
controller's fixed identity, then require **`IsWindowVisible(hwnd)`** before contributing its
rect. Found-and-visible → collect rect; otherwise → contribute nothing.

⚠️ **Do NOT use mere existence as the test.** A window hidden to the tray (WPF `Hide()`)
**is still returned by `EnumWindows`** — it merely lacks `WS_VISIBLE`. Without the visibility
check, "minimise to tray" would keep reserving desktop space for an invisible controller.
`IsWindowVisible` is what distinguishes "closed / hidden" from "open".

Precedent to copy: `Win32/DesktopWindowLocator.cs` already uses `EnumWindows` +
`FindWindowEx` to find shell windows by class name.

**Contract on the controller**: it must expose a **stable, unique identity** to match on.

⚠️ **Corrected during Phase 1 — the original plan here was wrong.** This section used to say to
fix the window class via `HwndSourceParameters.WindowClass`. **That member does not exist**; the
compiler rejects it (`CS0117`). WPF offers no managed way to choose a window class, and the one it
does register is `HwndWrapper[<process>;;<guid>]` — a fresh GUID every run, hence useless as a key.
(`HwndSource.FromHwnd` on a self-created `CreateWindowEx` window returns `null`, so hand-rolling
the HWND is not a way out either.)

**Therefore the frozen contract is the window TITLE**, verified working:
`FindWindow(null, "DesktopMediaController")` matches, and the constant lives in
`WidgetWindow.WindowTitle` with a "do not rename" comment. It must never be derived from user data.
Still always pair it with `IsWindowVisible` as described above.

Alternative considered and rejected: a heartbeat file / named pipe. More moving parts, and it
needs its own staleness/lock handling, for zero benefit over "is the window there".

### 6.3 Two known traps

**Trap 1 — the packer only ever slides RIGHT.**
`PackRowMajor` L314: `cursorX += cellW` is the *only* evasion. When the row is exhausted it
wraps to the next row band (L299-304); when the bottom is reached the group is left
**unplaced** and keeps its current position (L306 → `unplaced`).

That is fine for pinned fences (which are laid out first and generally hug the left edge),
but the controller is **freely movable (G4)** — if the user parks it on the right side of the
screen, sliding right runs into the screen edge and the box wraps to a lower row band, or
ends up unplaced. Two acceptable fixes:

- (a) Extend evasion to pick the **nearest free direction** (left/up before wrapping), or
- (b) Constrain controller placement to the left region / accept the wrap.

(a) is the honest fix; it also benefits pinned-fence packing. **Decide before implementing.**

**Trap 2 — startup order.**
If DesktopOrganizer packs while the controller is not yet open, nothing is reserved; when the
controller opens later, already-placed icons **do not move aside**. Options: accept the rule
("re-tidy to apply"), or have the controller's appearance trigger a re-arrange. Recommend the
former first (no cross-process triggering), with a **settings toggle** ("避开桌面控制器",
default on) so the behaviour can be turned off entirely.

### 6.4 Which monitor

Obstacles are per-screen in spirit (each fence packs inside its own rect), so the controller's
rect should only be contributed to the packer pass for the monitor its window actually
overlaps. Verify this against how `ArrangeAll` iterates monitors before coding.

## 7. Phases

| Phase | Deliverable | Exit criterion |
|---|---|---|
| **0. Unblock** | QQ音乐 version + SMTC switch; re-probe sessions | A session appears and reports title/artist/position |
| **1. Skeleton** | New project with TFM `net9.0-windows10.0.19041.0`; a top-level always-on-top window that drags, remembers its position, and shows a hardcoded title | Draggable across both monitors at correct DPI; position survives restart |
| **2. Display + control** | SMTC wired: cover/title/artist/progress + ⏮ ⏯ ⏭ + seek | Real playback follows the UI and vice versa, no UI thread hitching |
| **3. Lyrics** | LRCLIB client ported to C# (title-directed search + gate + fuzzy title + 503 retry); 3-line scrolling display | Spot-check against a 100-song list; expect ~87% synced |
| **4. Integration** | Stable window class; `DesktopLayoutService` collects the controller rect into `obstacles`; settings toggle; evasion-direction decision from §6.3 | Icons never overlap the controller; with the controller closed, packing is byte-identical to today |

Each phase should be committed separately (Conventional Commits, `feat:`/`fix:`); phase 4 is
the only one that touches existing files and must not regress the pinned-fence tests
(`DesktopLayoutSnapshotTests`, which reverse-verifies the obstacle input at L135).

## 8. Reference projects (survey)

| Project | Why it matters | What to take |
|---|---|---|
| **FluentFlyout** (~1.3k★, active 2026) | Closest mature peer: SMTC media flyout for Win11 | Compact vs full layouts, seek-drag UX, **cover-art-driven accent colour**, auto-hide when nothing plays, position options |
| **MusicBar** | Taskbar-attached controller | Three size tiers, rounded/alpha tuning |
| **Simple-Music-Widget** | Small footprint overlay | Single-instance guard, tray behaviour |
| **Lyricify Lite** | Lyrics-focused | The correct approach to lyric sourcing/display on desktop |
| DesktopFrames+ / NoFences / openFences | Desktop fence apps | Reference only — **none of them solve "detect a foreign window and avoid it"**, so §6 is original work here, not something to copy |

## 9. Open questions

- **Q1** — New project **inside this solution** (`src/DesktopMediaController/`, sharing
  `Win32/` conventions and the publish flow) or a **separate repo**? Leaning: same solution,
  separate `.csproj` with its own TFM (§5.2 option B) and its own exe.
- **Q2** — Default position and size on first launch? Freely movable after (G4), but it needs
  a sane initial placement.
- **Q3** — Volume control: SMTC does not expose per-app volume. Would need Core Audio
  (`IAudioEndpointVolume`) — extra scope. Probably out.
- **Q4** — Should G2 lyrics live in the same window card, or a separate attachable panel?
  (The wallpaper shipped the embedded form; keep consistency if cheap.)
- **Q5** — Does the controller need to survive Explorer restarts? (DesktopOrganizer already
  watches for that; the controller is top-level and probably does not.)

## 10. Explicit statements

- **No code has been written.** This spec is the output of read-only research
  (official docs, web survey, and inspection of this repo).
- **Existing files unchanged** — the app, its tests, and the wallpaper project are all
  untouched.
- Invariants that must hold when this is implemented: P/Invoke confined to `Win32/`, zero
  build warnings (`TreatWarningsAsErrors`), pure logic in `Core` with unit tests,
  Conventional Commits, single `main` branch.
