using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using DesktopOrganizer.Core.Layout;

namespace DesktopOrganizer.Win32;

public sealed class SysListView32Provider : IDesktopIconProvider, IDisposable
{
    // Mutable because an Explorer restart invalidates every cached value: the hwnd dies, the
    // client origin changes and the marshaller's cross-process channel points at a dead PID.
    // TryRecover re-runs Discover to rebind all of them.
    private IntPtr _hwnd;
    private bool _available;
    private IReadOnlyDictionary<string, string> _nameToPath = new Dictionary<string, string>();
    private int _clientLeft;
    private int _clientTop;

    // Auto-arrange probe cache (see AutoArrangeOn): 0 = never probed.
    private const long StyleProbeTtlMs = 1000;
    private bool _autoArrangeOn;
    private long _styleProbedAt;
    private LvItemMarshaller? _marshaller;

    // Explorer 的"对齐图标到网格"晶格（listview client 坐标）：格子 (Cx,Cy)，原点 (Ox,Oy)。
    // 相位持续自校正（2026-09-06 二次漂移事故）：一次性标定会在开机自动整理后、Explorer
    // 异步修正落位之前读到我们自己写入的任意位置，把错误相位锁死终身（实测锁定 x≡42 mod 76、
    // y≡6 mod 82，而真相位 x≡22、y≡2，恒定漂移半格以内）。现改为：记录写入的原始坐标，
    // 回读 ≠ 写入值 = Explorer 做了网格修正 = 权威相位确认；无确认信号时多数投票兜底。
    private int _gridCx, _gridCy, _gridOx, _gridOy;
    private bool _gridKnown;
    private readonly Dictionary<int, PointI> _lastWrittenRaw = new(); // index → 我们写入的 client 原始坐标

    public SysListView32Provider()
    {
        Discover();
    }

    private void Discover()
    {
        _marshaller?.Dispose();
        _marshaller = null;
        _styleProbedAt = 0;
        _lastWrittenRaw.Clear(); // Explorer 重启后索引含义可能已变，旧记录不可作为修正信号
        try
        {
            _hwnd = DesktopWindowLocator.FindDesktopListView();
            _available = _hwnd != IntPtr.Zero;
            if (_available)
            {
                // listview client (0,0) = 虚拟屏左上角（多显示器时可能为负，如 -2560）
                NativeMethods.GetWindowRect(_hwnd, out var r);
                _clientLeft = r.Left;
                _clientTop = r.Top;
            }
            else
            {
                _clientLeft = 0;
                _clientTop = 0;
            }
            // Resolve display-name -> file path via Shell. Guarded so a failure (e.g. a
            // virtual item that makes shell calls throw) never breaks desktop availability.
            try
            {
                _nameToPath = _available ? DesktopShellEnumerator.DisplayNameToPath() : new Dictionary<string, string>();
            }
            catch (Exception)
            {
                _nameToPath = new Dictionary<string, string>();
            }
        }
        catch (DesktopWindowNotFoundException)
        {
            _hwnd = IntPtr.Zero; _available = false; _nameToPath = new Dictionary<string, string>();
            _clientLeft = 0; _clientTop = 0;
        }
    }

    /// <summary>
    /// Re-reads the listview's window origin. The desktop listview's client (0,0) maps to the
    /// virtual screen's top-left, which moves whenever monitors are added, removed or rearranged —
    /// including a monitor that disappears and comes back while the app keeps running. The origin
    /// used to be cached once in <see cref="Discover"/>: started while a monitor was absent, every
    /// later read AND write was offset by exactly that monitor's width — the whole desktop
    /// silently landed on the wrong screen, and because reads carried the identical offset the
    /// app's own consistency checks (rescue, refresh, same-position skip) all saw a healthy
    /// layout. GetWindowRect is a local win32k query (no cross-process round trip), so re-probing
    /// per read/write is effectively free. A failed probe keeps the previous origin — never
    /// degrade to (0,0) on a transient error.
    /// </summary>
    private void RefreshClientOrigin()
    {
        if (!_available || !NativeMethods.IsWindow(_hwnd)) return;
        try
        {
            if (NativeMethods.GetWindowRect(_hwnd, out var r))
            {
                _clientLeft = r.Left;
                _clientTop = r.Top;
            }
        }
        catch { /* keep the previous origin */ }
    }

    public IntPtr Handle => _hwnd;

    /// <summary>
    /// Available AND the cached window handle still alive. After an Explorer restart the old
    /// <c>HWND</c> is dead (or recycled), so availability must be verified per query rather than
    /// cached from construction time — otherwise the app would keep issuing calls into a dead
    /// window forever.
    /// </summary>
    public bool IsAvailable => _available && NativeMethods.IsWindow(_hwnd);
    public int IconSpacingX => Spacing(1);
    public int IconSpacingY => Spacing(0);

    private int Spacing(int which)
    {
        if (!_available) return 96;
        // wParam must be 0 (FALSE) to get the LARGE-icon spacing the desktop actually uses.
        // wParam=1 returns SMALL-icon spacing (e.g. 96x33), which crushes rows together.
        NativeMethods.SendMessageTimeout(_hwnd, NativeMethods.LVM_GETITEMSPACING, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, 2000, out var res);
        var v = (int)res;
        var raw = which == 1 ? (v & 0xFFFF) : (v >> 16);
        // Guard against implausibly small spacing (degrades to a safe default).
        return raw < 60 ? 96 : raw;
    }

    public int Count
    {
        get
        {
            if (!_available) return 0;
            NativeMethods.SendMessageTimeout(_hwnd, NativeMethods.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero,
                NativeMethods.SMTO_ABORTIFHUNG, 2000, out var c);
            return (int)c;
        }
    }

    public IReadOnlyList<DesktopIcon> GetIcons()
    {
        var result = new List<DesktopIcon>();
        if (!_available) return result;
        RefreshClientOrigin(); // monitor topology may have moved the listview since last tick
        EnsureMarshaller();
        RefreshGridSpacing(); // one extra cross-process call per 2 s tick — negligible
        var confirmed = new List<PointI>();  // 回读 ≠ 写入 → Explorer 修正过 → 权威相位样本
        var candidates = new List<PointI>(); // 全体可见图标 → 多数投票样本
        var consumed = new List<int>();      // 已消费的写入记录
        var n = Count;
        for (var i = 0; i < n; i++)
        {
            try
            {
                var name = _marshaller!.ReadItemText(_hwnd, i, NativeMethods.LVM_GETITEMTEXTW);
                var (x, y) = _marshaller.ReadItemPosition(_hwnd, i);
                if (y > -30000)
                {
                    var raw = new PointI(x, y);
                    candidates.Add(raw);
                    if (_lastWrittenRaw.TryGetValue(i, out var written))
                    {
                        if (written.X != x || written.Y != y)
                        {
                            confirmed.Add(raw); // Explorer 把它挪到了自己的格点上
                            consumed.Add(i);
                        }
                        // 读 == 写：修正尚未落地（或对齐关闭），保留记录等下一轮确认
                    }
                }
                _nameToPath.TryGetValue(name, out var path);
                // client 坐标 → 屏幕坐标（供上层与 WPF/屏幕坐标一致）
                result.Add(new DesktopIcon(i, name, path, new PointI(x + _clientLeft, y + _clientTop)));
            }
            catch (Win32Exception) { /* skip one icon, keep going */ }
        }
        foreach (var i in consumed) _lastWrittenRaw.Remove(i);
        UpdateLatticePhase(confirmed, candidates);
        return result;
    }

    public PointI GetPosition(int index)
    {
        RefreshClientOrigin();
        EnsureMarshaller();
        var (x, y) = _marshaller!.ReadItemPosition(_hwnd, index);
        // 拖动松手后的回读路径：Explorer 若修正了我们的写入，这里即刻确认权威相位，
        // 让下一次手势就用上正确网格（不等 2s 刷新 tick）。
        if (y > -30000 && _lastWrittenRaw.TryGetValue(index, out var written)
            && (written.X != x || written.Y != y))
        {
            _lastWrittenRaw.Remove(index);
            if (_gridCx > 0 && _gridCy > 0)
                ApplyPhase(ResolveLatticePhase(new List<PointI> { new(x, y) }, new List<PointI>(), _gridCx, _gridCy, _gridOx, _gridOy, known: false));
        }
        return new PointI(x + _clientLeft, y + _clientTop); // client → screen
    }

    public bool IsAutoArrangeOn
    {
        get
        {
            if (!_available) return true;
            var style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
            return (style & NativeMethods.LVS_AUTOARRANGE) != 0;
        }
    }

    /// <summary>
    /// Turns off the desktop listview's "Auto arrange" style so <see cref="SetPosition"/>
    /// stops being ignored. Only clears the style bit if it is currently set; a no-op
    /// otherwise. Returns true when auto-arrange is off afterwards.
    /// </summary>
    public bool DisableAutoArrange()
    {
        if (!_available) return false;
        var style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
        if ((style & NativeMethods.LVS_AUTOARRANGE) == 0) return true;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_STYLE, style & ~NativeMethods.LVS_AUTOARRANGE);
        var after = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
        var off = (after & NativeMethods.LVS_AUTOARRANGE) == 0;
        if (off) { _autoArrangeOn = false; _styleProbedAt = Environment.TickCount64; }
        return off;
    }

    public void SetPosition(int index, PointI screenPos)
    {
        if (!_available) return;
        RefreshClientOrigin(); // write in the CURRENT topology, not the one at startup
        // The style query is a cross-process round trip into Explorer, and SetPosition is called once
        // per icon per frame while a box is dragged (and once per icon on every arrange) — querying
        // it inline roughly doubled the cost of a drag frame. The bit only changes when the user
        // toggles it by hand in Explorer, so a short-lived cache is safe and keeps the hot path at
        // exactly one cross-process call (the LVM_SETITEMPOSITION itself).
        if (AutoArrangeOn())
            throw new DesktopAutoArrangeException(
                "Desktop has 'Auto arrange' ON — positions are ignored. Turn it off (right-click desktop → View → uncheck Auto arrange) and retry.");
        EnsureMarshaller();
        // screen → client（listview 原点在虚拟屏左上角，多显示器时可能为负）
        var rawX = screenPos.X - _clientLeft;
        var rawY = screenPos.Y - _clientTop;
        // 屏内目标先吸附到 Explorer 的网格晶格：当"对齐图标到网格"开启时，Explorer 会把每次写入
        // 重新量化到最近格位，整组图标最多漂移半格（2026-09-06 拖动漂移事故），且该设置在
        // Explorer 每次重启后都会静默回滚为开。写入预先吸附的坐标后，无论开关状态，我们的意图与
        // Explorer 的落点都一致。折叠停靠位（屏外 y≈-32000）不属于网格，必须保持精确值。
        if (_gridKnown && rawY > -30000)
        {
            var snapped = SnapToLattice(new PointI(rawX, rawY), _gridCx, _gridCy, _gridOx, _gridOy);
            rawX = snapped.X;
            rawY = snapped.Y;
        }
        // 记录写入值：下一次回读若与此不同，即证明 Explorer 做了网格修正（权威相位信号）。
        _lastWrittenRaw[index] = new PointI(rawX, rawY);
        _marshaller!.SetItemPosition(_hwnd, index, rawX, rawY);
    }

    /// <summary>Nearest lattice cell: lattice = origin + k·pitch. Pure static so the rounding
    /// semantics are unit-testable without a real Explorer. Rounding MUST be half-up
    /// (floor(v+0.5)), NOT banker's rounding: a drag-restored group shares one phase f, and
    /// floor(f+0.5) is the same for every member, so the whole group shifts rigidly — with
    /// Math.Round, a group whose phase is exactly 0.5 splits apart (even/odd k parity).</summary>
    internal static PointI SnapToLattice(PointI raw, int cellCx, int cellCy, int originX, int originY)
    {
        if (cellCx <= 0 || cellCy <= 0) return raw; // unusable pitch — identity, never invent one
        var kx = (int)Math.Floor((raw.X - originX) / (double)cellCx + 0.5);
        var ky = (int)Math.Floor((raw.Y - originY) / (double)cellCy + 0.5);
        return new PointI(originX + kx * cellCx, originY + ky * cellCy);
    }

    /// <summary>The displacement a whole group takes when told to move by <paramref name="d"/>:
    /// for any start point P already on the lattice, snap(P + d) − P is exactly this — the phase
    /// cancels out (floor(k + x) = k + floor(x) for integer k). The drag snap preview uses it to
    /// show the drop spot before release; the release itself still trusts the icons' MEASURED
    /// displacement (the preview is a prediction, the readback is the authority). Same half-up
    /// rounding contract as <see cref="SnapToLattice"/>.</summary>
    internal static PointI SnapDeltaToLattice(PointI d, int cellCx, int cellCy)
    {
        if (cellCx <= 0 || cellCy <= 0) return d; // unusable pitch — identity, never invent one
        return new PointI(
            cellCx * (int)Math.Floor(d.X / (double)cellCx + 0.5),
            cellCy * (int)Math.Floor(d.Y / (double)cellCy + 0.5));
    }

    public bool TryGetLatticeCell(out int cellCx, out int cellCy)
    {
        cellCx = _gridCx;
        cellCy = _gridCy;
        return _gridKnown && cellCx > 0 && cellCy > 0;
    }

    private void RefreshGridSpacing()
    {
        try
        {
            var (cx, cy) = _marshaller!.GetItemSpacing(_hwnd);
            if (cx > 0 && cy > 0) { _gridCx = cx; _gridCy = cy; }
        }
        catch { /* keep the previous pitch; _gridKnown only flips via ObserveGrid */ }
    }

    private void UpdateLatticePhase(List<PointI> confirmed, List<PointI> candidates)
    {
        if (_gridCx <= 0 || _gridCy <= 0) return;
        var phase = ResolveLatticePhase(confirmed, candidates, _gridCx, _gridCy, _gridOx, _gridOy, _gridKnown);
        ApplyPhase(phase);
    }

    private void ApplyPhase((int Ox, int Oy)? phase)
    {
        if (phase is { } p)
        {
            _gridOx = p.Ox;
            _gridOy = p.Oy;
            _gridKnown = true;
        }
    }

    private static int Mod(int v, int m) => ((v % m) + m) % m;

    /// <summary>Lattice phase decision, pure and static for unit testing. Precedence:
    /// 1) confirmed samples (read ≠ what we wrote ⇒ Explorer itself re-quantized the icon onto
    /// its true lattice — authoritative; adopt their mode unconditionally);
    /// 2) unknown phase ⇒ most-voted candidate phase (provisional, corrected later);
    /// 3) known phase ⇒ only switch when the current phase loses the vote decisively
    /// (top other bucket ≥ 3 votes AND strictly more than current) — covers icon-size/DPI
    /// changes without thrashing while corrections are still in flight.</summary>
    internal static (int Ox, int Oy)? ResolveLatticePhase(
        IReadOnlyList<PointI> confirmed, IReadOnlyList<PointI> candidates,
        int cellCx, int cellCy, int curOx, int curOy, bool known)
    {
        if (confirmed.Count > 0)
        {
            var (best, _) = VotePhase(confirmed, cellCx, cellCy);
            return best;
        }
        var (top, topCount) = VotePhase(candidates, cellCx, cellCy);
        if (topCount == 0) return null;
        if (!known) return top;
        var curCount = CountPhase(candidates, cellCx, cellCy, curOx, curOy);
        if (top != (curOx, curOy) && topCount >= 3 && curCount < topCount) return top;
        return null; // keep current phase
    }

    private static ((int Ox, int Oy), int) VotePhase(IReadOnlyList<PointI> samples, int cellCx, int cellCy)
    {
        var votes = new Dictionary<(int, int), int>();
        foreach (var s in samples)
        {
            var key = (Mod(s.X, cellCx), Mod(s.Y, cellCy));
            votes[key] = votes.GetValueOrDefault(key) + 1;
        }
        (int, int) best = default; var bestCount = 0;
        foreach (var kv in votes)
        {
            if (kv.Value > bestCount) { best = kv.Key; bestCount = kv.Value; }
        }
        return (best, bestCount);
    }

    private static int CountPhase(IReadOnlyList<PointI> samples, int cellCx, int cellCy, int ox, int oy)
    {
        var n = 0;
        foreach (var s in samples)
            if (Mod(s.X, cellCx) == ox && Mod(s.Y, cellCy) == oy) n++;
        return n;
    }

    /// <summary>Cached "auto arrange" probe (<see cref="SetPosition"/> explains why). Refresh after
    /// <see cref="StyleProbeTtlMs"/> so a manual toggle in Explorer is still picked up within a
    /// second — worst case a few writes in that window are ignored, and the next probe reports it.</summary>
    private bool AutoArrangeOn()
    {
        var now = Environment.TickCount64;
        if (_styleProbedAt != 0 && now - _styleProbedAt < StyleProbeTtlMs) return _autoArrangeOn;
        var style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
        _autoArrangeOn = (style & NativeMethods.LVS_AUTOARRANGE) != 0;
        _styleProbedAt = now;
        return _autoArrangeOn;
    }

    private void EnsureMarshaller()
    {
        if (_marshaller is not null) return;
        NativeMethods.GetWindowThreadProcessId(_hwnd, out var pid);
        _marshaller = new LvItemMarshaller(pid);
    }

    /// <summary>
    /// Re-acquires the desktop hook after an Explorer restart: the cached hwnd is dead, the
    /// desktop listview lives in a new (possibly different-PID) process, and the marshaller's
    /// cross-process channel is stale. Re-runs discovery from scratch and rebuilds the marshaller.
    /// A no-op (returning true) while the current handle is still alive.
    /// </summary>
    public bool TryRecover()
    {
        if (_available && NativeMethods.IsWindow(_hwnd)) return true;
        Discover();
        return _available;
    }

    public void Dispose() => _marshaller?.Dispose();
}
