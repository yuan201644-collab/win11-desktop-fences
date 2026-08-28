using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using DesktopOrganizer.Core.Layout;

namespace DesktopOrganizer.Win32;

public sealed class SysListView32Provider : IDesktopIconProvider, IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly bool _available;
    private readonly IReadOnlyDictionary<string, string> _nameToPath;
    private readonly int _clientLeft;
    private readonly int _clientTop;
    private LvItemMarshaller? _marshaller;

    public SysListView32Provider()
    {
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
    public bool IsAvailable => _available;
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

    public void SetPosition(int index, PointI screenPos)
    {
        if (!_available) return;
        var style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
        if ((style & NativeMethods.LVS_AUTOARRANGE) != 0)
            throw new DesktopAutoArrangeException(
                "Desktop has 'Auto arrange' ON — positions are ignored. Turn it off (right-click desktop → View → uncheck Auto arrange) and retry.");
        EnsureMarshaller();
        // screen → client（listview 原点在虚拟屏左上角，多显示器时可能为负）
        _marshaller!.SetItemPosition(_hwnd, index, screenPos.X - _clientLeft, screenPos.Y - _clientTop);
    }

    private void EnsureMarshaller()
    {
        if (_marshaller is not null) return;
        NativeMethods.GetWindowThreadProcessId(_hwnd, out var pid);
        _marshaller = new LvItemMarshaller(pid);
    }

    public void Dispose() => _marshaller?.Dispose();
}
