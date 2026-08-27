using System;
using System.Collections.Generic;
using System.ComponentModel;
using DesktopOrganizer.Core.Layout;

namespace DesktopOrganizer.Win32;

public sealed class SysListView32Provider : IDesktopIconProvider, IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly bool _available;
    private readonly Dictionary<string, string> _nameToPath;
    private LvItemMarshaller? _marshaller;

    public SysListView32Provider()
    {
        try
        {
            _hwnd = DesktopWindowLocator.FindDesktopListView();
            _available = _hwnd != IntPtr.Zero;
            _nameToPath = _available ? (Dictionary<string, string>)DesktopShellEnumerator.DisplayNameToPath() : new Dictionary<string, string>();
        }
        catch (DesktopWindowNotFoundException)
        {
            _hwnd = IntPtr.Zero; _available = false; _nameToPath = new Dictionary<string, string>();
        }
    }

    public IntPtr Handle => _hwnd;
    public bool IsAvailable => _available;
    public int IconSpacingX => Spacing(1);
    public int IconSpacingY => Spacing(0);

    private int Spacing(int which)
    {
        if (!_available) return 96;
        NativeMethods.SendMessageTimeout(_hwnd, NativeMethods.LVM_GETITEMSPACING, (IntPtr)1, IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, 2000, out var res);
        var v = (int)res;
        return which == 1 ? (v & 0xFFFF) : (v >> 16);
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
                result.Add(new DesktopIcon(i, name, path, new PointI(x, y)));
            }
            catch (Win32Exception) { /* skip one icon, keep going */ }
        }
        return result;
    }

    public PointI GetPosition(int index)
    {
        EnsureMarshaller();
        var (x, y) = _marshaller!.ReadItemPosition(_hwnd, index);
        return new PointI(x, y);
    }

    public void SetPosition(int index, PointI position)
    {
        if (!_available) return;
        var style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
        if ((style & NativeMethods.LVS_AUTOARRANGE) != 0)
            throw new DesktopAutoArrangeException(
                "Desktop has 'Auto arrange' ON — positions are ignored. Turn it off (right-click desktop → View → uncheck Auto arrange) and retry.");
        var lp = (IntPtr)((position.Y << 16) | (position.X & 0xFFFF));
        NativeMethods.SendMessageTimeout(_hwnd, NativeMethods.LVM_SETITEMPOSITION32, (IntPtr)index, lp,
            NativeMethods.SMTO_ABORTIFHUNG, 2000, out _);
    }

    private void EnsureMarshaller()
    {
        if (_marshaller is not null) return;
        NativeMethods.GetWindowThreadProcessId(_hwnd, out var pid);
        _marshaller = new LvItemMarshaller(pid);
    }

    public void Dispose() => _marshaller?.Dispose();
}
