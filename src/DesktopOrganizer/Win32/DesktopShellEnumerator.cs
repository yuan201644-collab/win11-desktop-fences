using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopOrganizer.Win32;

/// <summary>
/// Resolves desktop icon display-names to file paths and (.lnk) target apps.
///
/// Robustness note: the original implementation walked PIDLs with
/// IShellFolder.GetDisplayNameOf + StrRetToBuf. That path can raise a native
/// AccessViolation (Corrupted-State Exception) on virtual items such as the
/// Recycle Bin, which bypasses every managed catch and silently kills the app.
///
/// This version follows the approach used by community tools like
/// SortDesktopIcons: get each item as an IShellItem and let
/// IShellItem.GetDisplayName return a managed string (COM-allocated BSTR) —
/// no raw STRRET/union marshalling. .lnk targets are resolved through the
/// late-bound WScript.Shell COM object, which only ever throws managed
/// exceptions, never a native AV.
/// </summary>
internal static class DesktopShellEnumerator
{
    [Flags]
    internal enum SHCONTF : uint
    {
        FOLDER = 0x20,
        NONFOLDER = 0x40,
        INCLUDEHIDDEN = 0x80,
    }

    // Correct SIGDN values (not the zero-based enum some snippets use).
    internal enum SIGDN : uint
    {
        NORMALDISPLAY = 0x00000000,
        PARENTRELATIVEPARSING = 0x80018001,
        DESKTOPABSOLUTEPARSING = 0x80028000,
        PARENTRELATIVEEDITING = 0x80031001,
        DESKTOPABSOLUTEEDITING = 0x8004c000,
        FILESYSPATH = 0x80058000,
        URL = 0x80068000,
        PARENTRELATIVEFORADDRESSBAR = 0x8007c001,
        PARENTRELATIVE = 0x80080001,
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    internal interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, out uint pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, SHCONTF grfFlags, out IEnumIDList ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, SHGDN_FORCOMPAT uFlags, out IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, SHGDN_FORCOMPAT uFlags, out IntPtr ppidlOut);
    }

    // Placeholder flag type kept only so the IShellFolder signature above compiles
    // without dragging in the full SHGDN enum; we no longer call GetDisplayNameOf.
    internal enum SHGDN_FORCOMPAT : uint { NORMAL = 0x0, FORPARSING = 0x8000 }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F2-0000-0000-C000-000000000046")]
    internal interface IEnumIDList
    {
        [PreserveSig] int Next(uint celt, out IntPtr rgelt, out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumIDList ppenum);
    }

    // IShellItem — same vtable layout as SortDesktopIcons / the Windows SDK.
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    internal interface IShellItem
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object BindToHandler(System.Runtime.InteropServices.ComTypes.IBindCtx pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid);
        IShellItem GetParent();
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetDisplayName(SIGDN sigdnName);
        [return: MarshalAs(UnmanagedType.U4)]
        uint GetAttributesOf(uint sfgaoMask);
    }

    [DllImport("shell32.dll")]
    internal static extern int SHGetDesktopFolder(out IShellFolder? ppshf);

    [DllImport("shell32.dll", PreserveSig = true)]
    internal static extern int SHCreateItemFromIDList(
        IntPtr pidl,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? ppv);

    /// <summary>
    /// Maps each desktop icon's display name to its file-system / parsing path.
    /// Returns an empty map on any failure — never throws, never raises a CSE.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DisplayNameToPath()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            SHGetDesktopFolder(out IShellFolder? desktop);
            if (desktop is null) return map;
            try
            {
                desktop.EnumObjects(IntPtr.Zero, SHCONTF.FOLDER | SHCONTF.NONFOLDER | SHCONTF.INCLUDEHIDDEN, out IEnumIDList? enumId);
                if (enumId is null) return map;
                try
                {
                    while (enumId.Next(1, out IntPtr pidl, out uint fetched) == 0 && fetched == 1)
                    {
                        try
                        {
                            // Robust per-item resolution via IShellItem (no raw STRRET).
                            var hr = SHCreateItemFromIDList(pidl, typeof(IShellItem).GUID, out IShellItem? item);
                            if (hr != 0 || item is null) continue;
                            var display = item.GetDisplayName(SIGDN.NORMALDISPLAY);
                            var path = item.GetDisplayName(SIGDN.DESKTOPABSOLUTEPARSING);
                            if (!string.IsNullOrEmpty(display) && !string.IsNullOrEmpty(path) && !map.ContainsKey(display))
                                map[display] = path;
                        }
                        catch (Exception)
                        {
                            // A single virtual/odd item must not abort the whole enumeration.
                        }
                        finally { Marshal.FreeCoTaskMem(pidl); }
                    }
                }
                finally { Marshal.ReleaseComObject(enumId); }
            }
            finally { Marshal.ReleaseComObject(desktop); }
        }
        catch (Exception)
        {
            // Shell unavailable (non-Windows / unexpected) — degrade gracefully.
        }
        return map;
    }

    /// <summary>
    /// Resolves a desktop item path to the program it launches (.exe name), used by the
    /// classifier. Returns null when it can't be resolved — the caller falls back to
    /// extension/keyword rules. Never raises a native exception.
    /// </summary>
    public static string? LinkTargetAppFromPath(string path)
    {
        try
        {
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                // Late-bound WScript.Shell — the most stable shortcut resolver on Windows.
                // Returns a managed COMException at worst, never a native AV.
                var wsType = Type.GetTypeFromProgID("WScript.Shell");
                if (wsType is null) return null;
                // Note: no `using` here — these are COM RCWs released by the GC. Wrapping
                // `dynamic` in `using` would force a runtime IDisposable probe we don't need.
                var shell = Activator.CreateInstance(wsType);
                if (shell is null) return null;
                var shortcut = ((dynamic)shell).CreateShortcut(path);
                if (shortcut is null) return null;
                string? target = ((dynamic)shortcut).TargetPath as string;
                return string.IsNullOrWhiteSpace(target) ? null : System.IO.Path.GetFileName(target);
            }
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return System.IO.Path.GetFileName(path);
        }
        catch (Exception)
        {
            // Some links can't be resolved (broken target, UWP, etc.) — just skip.
        }
        return null;
    }
}
