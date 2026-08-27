using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DesktopOrganizer.Win32;

internal sealed class SafeRemoteBufferHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly IntPtr _process;
    private readonly IntPtr _size;

    internal SafeRemoteBufferHandle(IntPtr process, IntPtr ptr, IntPtr size) : base(true)
    {
        _process = process;
        _size = size;
        SetHandle(ptr);
    }

    protected override bool ReleaseHandle()
        => NativeMethods.VirtualFreeEx(_process, handle, _size, 0x8000); // MEM_RELEASE
}
