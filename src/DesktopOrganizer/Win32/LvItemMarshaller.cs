using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DesktopOrganizer.Win32;

internal sealed class LvItemMarshaller : IDisposable
{
    private readonly SafeProcessHandle _process;
    // 持久远程缓冲：读位置用（Explorer 同步写入 POINT 后我们读取）
    private readonly SafeRemoteBufferHandle _posBuf;

    internal LvItemMarshaller(int processId)
    {
        var h = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_VM_OPERATION | NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_VM_WRITE,
            false, processId);
        if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        _process = new SafeProcessHandle(h, ownsHandle: true);
        _posBuf = Alloc((IntPtr)8);
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
        Send(listView, NativeMethods.LVM_GETITEMPOSITION, (IntPtr)index, _posBuf.DangerousGetHandle());
        var bytes = new byte[8];
        if (!NativeMethods.ReadProcessMemory(_process.DangerousGetHandle(), _posBuf.DangerousGetHandle(), bytes, (IntPtr)8, out _))
            return (0, 0);
        return (BitConverter.ToInt32(bytes, 0), BitConverter.ToInt32(bytes, 4));
    }

    internal void SetItemPosition(IntPtr listView, int index, int x, int y)
    {
        // 实测结论（多轮 A/B 实验）：
        // - 0x103E 打包坐标：消息返回成功但图标位置被忽略（不动）
        // - 0x100F + POINT*：Explorer 把 lParam 当"打包坐标"拆（图标落点 = 指针地址高16位），
        //   证明桌面 listview 对 0x100F 的语义就是 lParam=MAKELPARAM(x,y)（非文档的 POINT*）
        // 因此正确用法：0x100F + 打包坐标。
        var lp = (IntPtr)((y << 16) | (x & 0xFFFF)); // MAKELPARAM(x, y)
        var result = Send(listView, NativeMethods.LVM_SETITEMPOSITION, (IntPtr)index, lp);
        if (result == IntPtr.Zero)
            throw new InvalidOperationException($"LVM_SETITEMPOSITION returned FALSE (Explorer refused) for index {index}");
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

    private static IntPtr Send(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        const int ERROR_TIMEOUT = 1460; // explorer was momentarily busy/hung during a bulk re-layout
        int lastError = 0;
        // A transient timeout must not abort an entire arrange/collapse — retry a few times.
        // SetItemPosition/GetItemPosition are idempotent on retry (same target), so repeating is safe.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (NativeMethods.SendMessageTimeout(hwnd, msg, w, l,
                    NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_NORMAL, 2000, out var result) != IntPtr.Zero)
                return result;
            lastError = Marshal.GetLastWin32Error();
            if (lastError != ERROR_TIMEOUT) break; // a non-timeout failure is real — don't spin
            Thread.Sleep(100);
        }
        throw new Win32Exception(lastError);
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

    public void Dispose()
    {
        _posBuf.Dispose();
        _process.Dispose();
    }
}
