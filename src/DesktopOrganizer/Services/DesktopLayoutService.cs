using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DesktopOrganizer.Core.Classification;
using DesktopOrganizer.Core.Config;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Win32;

namespace DesktopOrganizer.Services;

/// <summary>One icon's placement decided by an arrange pass: the icon, its category, the box it
/// belongs to, and where its position was written.</summary>
public sealed record ArrangeEntry(DesktopIcon Icon, Category Category, string Title, PointI Target);

/// <summary>Everything one arrange produced: the placements in write order, the rectangle every
/// pinned box actually used — which can be larger than the stored one when the box had to grow to
/// hold its icons (see <see cref="DesktopLayoutService.ArrangeAll"/>) — and the icons the auto
/// packer could not place because the remembered boxes left no room. Those keep their current
/// position (never stacked onto a shared cell) and are reported so the UI can suggest a fix.</summary>
public sealed record ArrangeOutcome(
    IReadOnlyList<ArrangeEntry> Entries,
    IReadOnlyDictionary<string, RectI> FenceRects,
    IReadOnlyList<DesktopIcon> Unplaced);

public sealed class DesktopLayoutService
{
    private readonly IDesktopIconProvider _provider;
    private readonly ClassifierEngine _engine;
    private readonly ClassifierConfig _config;
    private SoftwareGroupingConfig _grouping;

    public DesktopLayoutService(IDesktopIconProvider provider, ClassifierEngine engine, ClassifierConfig config)
    {
        _provider = provider;
        _engine = engine;
        _config = config;
        _grouping = SoftwareGroupStore.Load(SoftwareGroupStore.DefaultFilePath);
    }

    /// <summary>Swaps in a (possibly user-edited) grouping config with no re-layout until ArrangeIntoFence runs.</summary>
    public void SetGrouping(SoftwareGroupingConfig grouping)
        => _grouping = grouping ?? SoftwareGroupStore.Default();

    /// <summary>
    /// Classifies a set of icons without touching their positions. Reused by the
    /// overlay controller so it can rebuild cluster boxes from live icon state
    /// (e.g. after a manual drag) without re-arranging the desktop.
    /// </summary>
    public IReadOnlyList<(DesktopIcon Icon, Category Category)> ClassifyAll(IReadOnlyList<DesktopIcon> icons)
    {
        var result = new List<(DesktopIcon, Category)>(icons.Count);
        foreach (var icon in icons)
        {
            var linkApp = icon.Path is not null ? DesktopShellEnumerator.LinkTargetAppFromPath(icon.Path) : null;
            var entry = new IconEntry(icon.Index, icon.Name, icon.Path ?? string.Empty, linkApp);
            result.Add((icon, _engine.Classify(entry, _config)));
        }
        return result;
    }

    /// <summary>A classified desktop item: which icon, its category, and the box title it belongs to.</summary>
    private sealed record Item(DesktopIcon Icon, Category Category, string Title);

    /// <summary>
    /// Enumerates and classifies the desktop ONCE and returns the items in box order + the chosen
    /// in-fence ordering. Every arrange path funnels through here, so the expensive part (resolving
    /// each .lnk/.url target) happens once per desktop and is additionally memoised by
    /// <see cref="DesktopShellEnumerator"/>.
    /// </summary>
    private List<Item> BuildItems(FenceSortMode sort, IReadOnlyCollection<string>? skipTitles)
    {
        var icons = _provider.GetIcons();
        var classified = ClassifyAll(icons);

        // Group by on-screen box (软件按用途拆成 办公/开发/影音/系统/其他 小框;文件夹/文件/其他各一个),
        // then by the chosen in-fence ordering. Software needs its resolved target exe to tell
        // purpose apart, so we resolve link targets here too — placement and overlay must agree on
        // the same box labels.
        var items = classified
            .Select(c => new
            {
                c.Icon,
                c.Category,
                Link = c.Icon.Path is null ? null : DesktopShellEnumerator.LinkTargetAppFromPath(c.Icon.Path),
            })
            .Select(x =>
            {
                var box = BoxGrouping.FromEntry(_grouping, x.Icon.Name, x.Icon.Path, x.Link);
                return (x.Icon, x.Category, Box: box);
            })
            .Where(x => skipTitles is null || !skipTitles.Contains(x.Box.Title, StringComparer.OrdinalIgnoreCase))
            .OrderBy(x => x.Box.Order);

        var ordered = sort switch
        {
            FenceSortMode.Type => items.ThenBy(x => x.Category).ThenBy(x => x.Icon.Name, StringComparer.OrdinalIgnoreCase),
            FenceSortMode.Modified => items.ThenBy(x => ModifiedTimeUtc(x.Icon)).ThenBy(x => x.Icon.Name, StringComparer.OrdinalIgnoreCase),
            _ => items.ThenBy(x => x.Icon.Name, StringComparer.OrdinalIgnoreCase),
        };
        return ordered.Select(x => new Item(x.Icon, x.Category, x.Box.Title)).ToList();
    }

    /// <summary>
    /// Auto-packs every item into <paramref name="fence"/> (row-major, lattice-aligned) and writes
    /// the positions. Placement only — the caller decides what to render.
    /// </summary>
    public IReadOnlyList<(DesktopIcon Icon, Category Category, PointI Target)> ArrangeIntoFence(
        RectI fence, int maxRows, FenceSortMode sort = FenceSortMode.Name,
        IReadOnlyCollection<string>? skipTitles = null)
    {
        if (!_provider.IsAvailable) return new List<(DesktopIcon, Category, PointI)>();
        DesktopShellEnumerator.ClearLinkTargetCache();
        var items = BuildItems(sort, skipTitles);
        var (placements, _) = PackRowMajor(items, fence, obstacles: null);

        var report = new List<(DesktopIcon, Category, PointI)>(items.Count);
        foreach (var (item, target) in placements)
        {
            if (target is not { } point) continue; // no room: leave it where it is
            _provider.SetPosition(item.Icon.Index, point); // DesktopAutoArrangeException bubbles to caller
            report.Add((item.Icon, item.Category, point));
        }
        return report;
    }

    /// <summary>
    /// Re-arranges ONLY the icons belonging to <paramref name="title"/> into the given
    /// <paramref name="bounds"/> rectangle (a user-pinned fence box). Column count follows the box
    /// width, rows follow the height budget, and every icon stays inside the rectangle — this is
    /// what the resize drag, the settings layout editor, and the expand path all use. Box membership
    /// and ordering match <see cref="ArrangeIntoFence"/> so a box never changes which icons it owns
    /// between auto-pack and manual layouts.
    /// </summary>
    /// <remarks>The rectangle is authoritative here and is NOT grown to fit: a rect the user is
    /// actively dragging or resizing must stay exactly what they see. Growing happens on arrange
    /// instead — see <see cref="ArrangeAll"/>.</remarks>
    public IReadOnlyList<(DesktopIcon Icon, PointI Target)> ArrangeOneFence(
        string title, RectI bounds, FenceSortMode sort = FenceSortMode.Name)
    {
        if (!_provider.IsAvailable) return new List<(DesktopIcon, PointI)>();
        DesktopShellEnumerator.ClearLinkTargetCache();
        var group = BuildItems(sort, skipTitles: null)
            .Where(i => string.Equals(i.Title, title, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (group.Count == 0) return new List<(DesktopIcon, PointI)>();

        var (targets, _) = PackInRect(group, bounds, grow: false, screen: default);

        var report = new List<(DesktopIcon, PointI)>(group.Count);
        for (var i = 0; i < group.Count; i++)
        {
            _provider.SetPosition(group[i].Icon.Index, targets[i]); // DesktopAutoArrangeException bubbles to caller
            report.Add((group[i].Icon, targets[i]));
        }
        return report;
    }

    /// <summary>
    /// The single-pass arrange used by 整理: the desktop is enumerated and classified ONCE, and that
    /// one snapshot feeds both the auto packer (every box that is not pinned) and every pinned box.
    /// Previously the pinned pass ran per box and re-classified the whole desktop each time, so one
    /// 整理 cost 2 + 2·(pinned boxes) full classification passes over every shortcut — the
    /// 2026-09-10 "整理卡死" incident had 11 pinned boxes and ~860 shell resolutions on the UI thread.
    /// </summary>
    /// <remarks>Remembered boxes are laid out FIRST and their rectangles become obstacles for the
    /// auto packer, so an auto box can never be dropped on top of one the user positioned. Auto
    /// icons that find no free lattice cell keep their current position and are reported in
    /// <see cref="ArrangeOutcome.Unplaced"/> — the arrange never stacks two icons on one cell.</remarks>
    /// <param name="pinnedRects">Title → stored rectangle for every remembered box. A rectangle too
    /// small for its icons grows (anchored at its top-left, kept inside <paramref name="screen"/>)
    /// and the used rectangle comes back in the outcome so the caller can persist it.</param>
    public ArrangeOutcome ArrangeAll(
        RectI fence, int maxRows, FenceSortMode sort,
        IReadOnlyDictionary<string, RectI> pinnedRects, RectI screen)
    {
        var entries = new List<ArrangeEntry>();
        var rects = new Dictionary<string, RectI>(StringComparer.OrdinalIgnoreCase);
        var unplaced = new List<DesktopIcon>();
        if (!_provider.IsAvailable) return new ArrangeOutcome(entries, rects, unplaced);

        // One resolution per shortcut per arrange: the memo is dropped here, then every lookup in
        // this pass (auto + pinned) hits it.
        DesktopShellEnumerator.ClearLinkTargetCache();
        var all = BuildItems(sort, skipTitles: null);

        // Pinned boxes first — their (possibly grown) rectangles are the obstacles the auto packer
        // has to dodge. Laying them out before the auto pass is what keeps a remembered box from
        // being covered by auto-packed icons.
        var obstacles = new List<RectI>(pinnedRects.Count);
        foreach (var (title, rect) in pinnedRects)
        {
            var group = all.Where(i => string.Equals(i.Title, title, StringComparison.OrdinalIgnoreCase)).ToList();
            RectI used;
            if (group.Count == 0)
            {
                used = rect; // an empty box still renders at its shape, so it still reserves space
            }
            else
            {
                var (targets, grown) = PackInRect(group, rect, grow: true, screen);
                used = grown;
                for (var i = 0; i < group.Count; i++)
                {
                    _provider.SetPosition(group[i].Icon.Index, targets[i]);
                    entries.Add(new ArrangeEntry(group[i].Icon, group[i].Category, group[i].Title, targets[i]));
                }
            }
            rects[title] = used;
            obstacles.Add(used);
        }

        // Everything else packs into whatever the remembered boxes left free.
        var autoItems = all.Where(i => !pinnedRects.ContainsKey(i.Title)).ToList();
        var (placements, _) = PackRowMajor(autoItems, fence, obstacles);
        foreach (var (item, target) in placements)
        {
            if (target is { } point)
            {
                _provider.SetPosition(item.Icon.Index, point);
                entries.Add(new ArrangeEntry(item.Icon, item.Category, item.Title, point));
            }
            else
            {
                // No free cell: the icon keeps its current position and stays visible. Clamping it
                // onto the last cell (the old behaviour) is what made icons pile into one blob.
                unplaced.Add(item.Icon);
            }
        }

        return new ArrangeOutcome(entries, rects, unplaced);
    }

    /// <summary>
    /// Lattice-aligned row-major packing. When the grid is known, the whole layout is computed in
    /// ICON-CELL coordinates: cursors start on the lattice and every step is a whole number of cells,
    /// so every icon target IS a lattice point — Explorer's per-write snapping becomes identity (no
    /// half-cell drift, no edge overflow; even the clamp boundaries are lattice points, so clamping
    /// can never knock a target off the grid). Boxes flow left→right and wrap to the next row
    /// (text-wrap style), separated by exactly one empty grid column / row — the smallest
    /// lattice-exact gap there is.
    /// </summary>
    /// <param name="obstacles">Rectangles the auto packer must not cover — the remembered (pinned)
    /// boxes, laid out before this pass. A candidate box that would overlap one is slid right one
    /// cell at a time; when the row is exhausted the cursor wraps; when the bottom is reached the
    /// group is left unplaced and its icons keep their current position (null target).</param>
    private (List<(Item Item, PointI? Target)> Placements, List<Item> Unplaced) PackRowMajor(
        List<Item> items, RectI fence, IReadOnlyList<RectI>? obstacles)
    {
        var cellW = _provider.IconSpacingX;
        var cellH = _provider.IconSpacingY;
        var headerPx = FenceHeader.HeaderPx;

        var hasLattice = _provider.TryGetLattice(out var gridCx, out var gridCy, out var gridOx, out var gridOy);
        static int CeilToLattice(int v, int pitch, int origin)
            => pitch > 0 ? origin + (int)Math.Ceiling((v - origin) / (double)pitch) * pitch : v;
        static int FloorToLattice(int v, int pitch, int origin)
            => pitch > 0 ? origin + (int)Math.Floor((v - origin) / (double)pitch) * pitch : v;

        var left = hasLattice ? CeilToLattice(fence.Left, gridCx, gridOx) : fence.Left;
        var maxX = hasLattice ? FloorToLattice(fence.Right - cellW, gridCx, gridOx) : fence.Right - cellW;
        // The first ICON row sits below the reserved title band, and IT is the lattice anchor —
        // the box's own top (iconY − headerPx) then stays inside the layout rect, so the drag
        // release's ClampFenceRect never shifts a bare-click restore (first-arrange regression).
        var top = hasLattice ? CeilToLattice(fence.Top + headerPx, gridCy, gridOy) : fence.Top + headerPx;
        var maxY = hasLattice ? FloorToLattice(fence.Bottom - cellH, gridCy, gridOy) : fence.Bottom - cellH;
        var maxRowsPerFence = Math.Max(3, fence.Height / Math.Max(1, cellH) - 1);

        var blocked = obstacles is { Count: > 0 } ? obstacles : null;

        // Group by box title; a group is placed as a unit. (The grouping is by title, NOT by
        // position in `items`: two boxes can share an Order and interleave, so placements are
        // recorded per ITEM rather than by index.) 
        var groups = items.GroupBy(x => x.Title).Select(g => g.ToList()).ToList();
        var placements = new List<(Item, PointI?)>(items.Count);
        var unplaced = new List<Item>();

        var cursorX = left;
        var iconY = top;      // y of the current row-of-boxes' first ICON row (lattice point)
        var rowRows = 0;      // tallest box's row count in the current row-of-boxes
        foreach (var group in groups)
        {
            var count = group.Count;
            var cols = PackColumns(count, maxRowsPerFence);
            var rows = Math.Max(1, (int)Math.Ceiling(count / (double)cols));
            var width = cols * cellW;
            var height = headerPx + rows * cellH;

            // Walk the cursor to the first spot this box fits: step right a cell while a remembered
            // box is in the way, wrap to the next row band when the row is exhausted, and stop once
            // the box's LAST ROW would fall past the layout rect (checking only the first row would
            // let the last rows be clamped onto each other — the 6-icon pile this test caught).
            var placed = false;
            for (var guard = 0; guard < 100_000; guard++)
            {
                // Wrap when this box's last column would start past the rightmost LEGAL column
                // (maxX): for lattice-aligned widths the far edge may reach maxX + cellW.
                if (cursorX > left && cursorX + width > maxX + cellW)
                {
                    cursorX = left;
                    iconY += rowRows * cellH + cellH; // one empty grid row between stacked boxes
                    rowRows = 0;
                }
                // iconY−headerPx is the box top; the last icon row must still land inside the rect.
                if (iconY + (rows - 1) * cellH > maxY) break;

                if (blocked is null
                    || !OverlapsAny(new RectI(cursorX, iconY - headerPx, width, height), blocked))
                {
                    placed = true;
                    break;
                }
                cursorX += cellW; // a remembered box is in the way — try the next column
            }

            if (placed)
            {
                for (var i = 0; i < count; i++)
                {
                    var x = Math.Clamp(cursorX + (i % cols) * cellW, left, Math.Max(left, maxX));
                    var y = Math.Clamp(iconY + (i / cols) * cellH, top, Math.Max(top, maxY));
                    placements.Add((group[i], new PointI(x, y)));
                }
                rowRows = Math.Max(rowRows, rows);
                cursorX += width + cellW; // one empty grid column between side-by-side boxes
            }
            else
            {
                foreach (var item in group) { placements.Add((item, null)); unplaced.Add(item); }
            }
        }
        return (placements, unplaced);
    }

    /// <summary>True when two rectangles intersect (touching edges do not count). The obstacle test
    /// for the auto packer.</summary>
    private static bool OverlapsAny(RectI box, IReadOnlyList<RectI> obstacles)
    {
        foreach (var o in obstacles)
            if (box.Left < o.Right && o.Left < box.Right && box.Top < o.Bottom && o.Top < box.Bottom)
                return true;
        return false;
    }

    /// <summary>
    /// Lays one box's icons inside <paramref name="bounds"/>. <paramref name="grow"/> is the
    /// arrange-time policy: enlarge the rectangle just enough to hold the icons instead of piling
    /// the overflow onto the last cell.
    /// </summary>
    private (List<PointI> Targets, RectI Rect) PackInRect(List<Item> group, RectI bounds, bool grow, RectI screen)
    {
        var cellW = _provider.IconSpacingX;
        var cellH = _provider.IconSpacingY;
        var headerPx = FenceHeader.HeaderPx;
        const int EdgePad = 8;

        var count = group.Count;
        var rect = grow ? GrowToFit(bounds, count, cellW, cellH, headerPx, screen) : bounds;

        // Column count follows the box width and must never exceed what that width can hold.
        // Widening past it (the old `while (cols < 16 …) cols++`) only pushed columns beyond
        // rect.Right, where the per-icon clamp piled them onto the column before them — three icons
        // sharing one lattice point in 其他软件 94×131 (2026-09-10).
        var innerW = Math.Max(cellW, rect.Width - EdgePad * 2);
        var innerH = Math.Max(cellH, rect.Height - headerPx - EdgePad);
        var cols = Math.Max(1, innerW / cellW);
        var maxRows = Math.Max(1, innerH / cellH);

        var targets = new List<PointI>(count);
        for (var i = 0; i < count; i++)
        {
            var x = Math.Clamp(rect.X + EdgePad + (i % cols) * cellW,
                rect.Left, Math.Max(rect.Left, rect.Right - cellW));
            var y = Math.Clamp(rect.Y + headerPx + (i / cols) * cellH,
                rect.Top, Math.Max(rect.Top, rect.Bottom - cellH));
            targets.Add(new PointI(x, y));
        }
        return (targets, rect);
    }

    /// <summary>
    /// Grows a pinned rectangle just enough to hold <paramref name="count"/> icons while staying
    /// inside <paramref name="screen"/>. Width first — a wider box keeps its top-left anchor, so the
    /// box the user positioned stays where they put it — then height when the screen caps the width.
    /// Returns the original rectangle when nothing needs to grow (the common case) or when even the
    /// whole screen cannot hold the icons (that arrange still clamps).
    /// </summary>
    private static RectI GrowToFit(RectI rect, int count, int cellW, int cellH, int headerPx, RectI screen)
    {
        const int EdgePad = 8;
        if (count <= 0 || cellW <= 0 || cellH <= 0) return rect;

        var cols = Math.Max(1, (rect.Width - EdgePad * 2) / cellW);
        var rows = Math.Max(1, (rect.Height - headerPx - EdgePad) / cellH);
        if (cols * rows >= count) return rect;

        var needCols = (int)Math.Ceiling(count / (double)rows);
        var widthByCols = needCols * cellW + EdgePad * 2;
        if (rect.X + widthByCols <= screen.Right)
            return rect with { Width = Math.Max(rect.Width, widthByCols) };

        // The screen caps the width: use every column available from rect.X and grow downwards.
        var usableCols = Math.Max(1, (screen.Right - rect.X - EdgePad * 2) / cellW);
        var needRows = (int)Math.Ceiling(count / (double)usableCols);
        var width = Math.Max(rect.Width, usableCols * cellW + EdgePad * 2);
        var height = Math.Max(rect.Height, Math.Min(needRows * cellH + headerPx + EdgePad, screen.Bottom - rect.Y));
        return rect with { Width = width, Height = height };
    }

    private static DateTime ModifiedTimeUtc(DesktopIcon icon)
    {
        try
        {
            if (icon.Path is null) return DateTime.MinValue;
            return File.GetLastWriteTimeUtc(icon.Path);
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Chooses the internal column count for one fence: compact (square-ish) for a handful of
    /// icons, but widened so a very large kind never grows taller than the visible area.
    /// </summary>
    private static int PackColumns(int count, int maxRowsPerFence)
    {
        if (count <= 1) return 1;
        var cols = Math.Clamp((int)Math.Ceiling(Math.Sqrt(count)), 1, 6);
        var rows = (int)Math.Ceiling(count / (double)cols);
        if (rows > maxRowsPerFence)
            cols = Math.Clamp((int)Math.Ceiling(count / (double)maxRowsPerFence), 1, 16);
        return Math.Max(1, cols);
    }
}
