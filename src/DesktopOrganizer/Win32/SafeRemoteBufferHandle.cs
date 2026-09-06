using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DesktopOrganizer.Win32;

internal sealed class SafeRemoteBufferHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly IntPtr _process;

    internal SafeRemoteBufferHandle(IntPtr process, IntPtr ptr, IntPtr size) : base(true)
    {
        _process = process;
        SetHandle(ptr);
    }

    /// <summary>Releases the remote buffer in Explorer's address space. MEM_RELEASE contract:
    /// dwSize MUST be 0 — passing the original allocation size makes VirtualFreeEx fail silently
    /// with ERROR_INVALID_PARAMETER (87) and leaks one 4 KB page per buffer in Explorer. This exact
    /// bug accumulated ~27 GB inside Explorer over ~15 h of 2 s polling (2026-09-06 incident);
    /// verified empirically: free(size=N) → ret=0 err=87, free(size=0) → ret=1.</summary>
    protected override bool ReleaseHandle()
        => NativeMethods.VirtualFreeEx(_process, handle, IntPtr.Zero, 0x8000); // MEM_RELEASE (dwSize must be 0)
}
