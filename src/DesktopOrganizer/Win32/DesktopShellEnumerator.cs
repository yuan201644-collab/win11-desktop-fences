using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopOrganizer.Win32;

internal static class DesktopShellEnumerator
{
    [StructLayout(LayoutKind.Explicit, Size = 264)]
    internal struct STRRET
    {
        [FieldOffset(0)] public uint uType;
        [FieldOffset(4)] public IntPtr pOleStr;
        [FieldOffset(4)] public IntPtr pStr;
        [FieldOffset(4)] public uint uOffset;
        [FieldOffset(4)] public IntPtr cStr;
    }

    [Flags]
    internal enum SHCONTF : uint
    {
        FOLDER = 0x20,
        NONFOLDER = 0x40,
        INCLUDEHIDDEN = 0x80,
    }

    internal enum SHGDN : uint
    {
        NORMAL = 0x0,
        FORPARSING = 0x8000,
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
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, SHGDN uFlags, out STRRET pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, SHGDN uFlags, out IntPtr ppidlOut);
    }

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

    [DllImport("shell32.dll")]
    internal static extern int SHGetDesktopFolder(out IShellFolder? ppshf);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    internal static extern int StrRetToBuf(ref STRRET pstr, IntPtr pidl, StringBuilder pszBuf, int cchBuf);

    public static IReadOnlyDictionary<string, string> DisplayNameToPath()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                        var display = GetDisplayName(desktop, pidl, SHGDN.NORMAL);
                        var path = GetDisplayName(desktop, pidl, SHGDN.FORPARSING);
                        if (!string.IsNullOrEmpty(display) && !string.IsNullOrEmpty(path))
                            map[display] = path;
                    }
                    finally { Marshal.FreeCoTaskMem(pidl); }
                }
            }
            finally { Marshal.ReleaseComObject(enumId); }
        }
        finally { Marshal.ReleaseComObject(desktop); }
        return map;
    }

    private static string GetDisplayName(IShellFolder folder, IntPtr pidl, SHGDN flags)
    {
        folder.GetDisplayNameOf(pidl, flags, out STRRET strret);
        var sb = new StringBuilder(260);
        StrRetToBuf(ref strret, pidl, sb, sb.Capacity);
        return sb.ToString();
    }
}
