using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DesktopOrganizer.Win32;

internal sealed class LvItemMarshaller : IDisposable
{
    private readonly SafeProcessHandle _process;

    internal LvItemMarshaller(int processId)
    {
        var h = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_VM_OPERATION | NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_VM_WRITE,
            false, processId);
        if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        _process = new SafeProcessHandle(h, ownsHandle: true);
    }

    internal string ReadItemText(IntPtr listView, int index, uint msg, int maxChars = NativeMethods.MAX_PATH)
    {
        var size = (IntPtr)(maxChars * 2);
        using var textBuf = Alloc(size);
        using var itemBuf = Alloc((IntPtr)Marshal.SizeOf<NativeMethods.LVITEMW>());
        var item = new NativeMethods.LVITEMW
        {
            mask = NativeMethods.LVIF_TEXT,
            iItem = index,
            pszText = textBuf.DangerousGetHandle(),
            cchTextMax = maxChars,
        };
        Write(item, itemBuf);
        Send(listView, msg, (IntPtr)index, itemBuf.DangerousGetHandle());
        var bytes = new byte[maxChars * 2];
        if (!NativeMethods.ReadProcessMemory(_process.DangerousGetHandle(), textBuf.DangerousGetHandle(), bytes, size, out _))
            return string.Empty;
        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    internal (int X, int Y) ReadItemPosition(IntPtr listView, int index)
    {
        var size = (IntPtr)8;
        using var buf = Alloc(size);
        Send(listView, NativeMethods.LVM_GETITEMPOSITION, (IntPtr)index, buf.DangerousGetHandle());
        var bytes = new byte[8];
        if (!NativeMethods.ReadProcessMemory(_process.DangerousGetHandle(), buf.DangerousGetHandle(), bytes, size, out _))
            return (0, 0);
        return (BitConverter.ToInt32(bytes, 0), BitConverter.ToInt32(bytes, 4));
    }

    private SafeRemoteBufferHandle Alloc(IntPtr size)
    {
        var ptr = NativeMethods.VirtualAllocEx(_process.DangerousGetHandle(), IntPtr.Zero, size,
            NativeMethods.MEM_COMMIT, NativeMethods.PAGE_READWRITE);
        if (ptr == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new SafeRemoteBufferHandle(_process.DangerousGetHandle(), ptr, size);
    }

    private void Write(NativeMethods.LVITEMW item, SafeRemoteBufferHandle remote)
    {
        var data = new byte[Marshal.SizeOf<NativeMethods.LVITEMW>()];
        var p = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.StructureToPtr(item, p, false);
            Marshal.Copy(p, data, 0, data.Length);
        }
        finally { Marshal.FreeHGlobal(p); }
        if (!NativeMethods.WriteProcessMemory(_process.DangerousGetHandle(), remote.DangerousGetHandle(), data, (IntPtr)data.Length, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void Send(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (NativeMethods.SendMessageTimeout(hwnd, msg, w, l,
                NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_NORMAL, 2000, out _) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal string RoundTripString(string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value + "\0");
        using var buf = Alloc((IntPtr)bytes.Length);
        if (!NativeMethods.WriteProcessMemory(_process.DangerousGetHandle(), buf.DangerousGetHandle(), bytes, (IntPtr)bytes.Length, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var outBytes = new byte[bytes.Length];
        if (!NativeMethods.ReadProcessMemory(_process.DangerousGetHandle(), buf.DangerousGetHandle(), outBytes, (IntPtr)bytes.Length, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return Encoding.Unicode.GetString(outBytes).TrimEnd('\0');
    }

    public void Dispose() => _process.Dispose();
}
