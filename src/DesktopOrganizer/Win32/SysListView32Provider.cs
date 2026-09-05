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

    public SysListView32Provider()
    {
        Discover();
    }

    private void Discover()
    {
        _marshaller?.Dispose();
        _marshaller = null;
        _styleProbedAt = 0;
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
        EnsureMarshaller();
        var n = Count;
        for (var i = 0; i < n; i++)
        {
            try
            {
                var name = _marshaller!.ReadItemText(_hwnd, i, NativeMethods.LVM_GETITEMTEXTW);
                var (x, y) = _marshaller.ReadItemPosition(_hwnd, i);
                _nameToPath.TryGetValue(name, out var path);
                // client 坐标 → 屏幕坐标（供上层与 WPF/屏幕坐标一致）
                result.Add(new DesktopIcon(i, name, path, new PointI(x + _clientLeft, y + _clientTop)));
            }
            catch (Win32Exception) { /* skip one icon, keep going */ }
        }
        return result;
    }

    public PointI GetPosition(int index)
    {
        EnsureMarshaller();
        var (x, y) = _marshaller!.ReadItemPosition(_hwnd, index);
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
        _marshaller!.SetItemPosition(_hwnd, index, screenPos.X - _clientLeft, screenPos.Y - _clientTop);
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
