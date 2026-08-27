using DesktopOrganizer.Win32;
using Xunit;
using System.Runtime.InteropServices;
using static DesktopOrganizer.Win32.NativeMethods;

namespace DesktopOrganizer.Tests.Win32;

public class NativeStructTests
{
    [Fact]
    public void LvItemW_HasExpectedFieldOffsets()
    {
        // mask(4) iItem(4) iSubItem(4) state(4) stateMask(4) pszText(8) cchTextMax(4)
        // iImage(4) lParam(8) ... -> size >= 44 on 64-bit
        Assert.True(Marshal.SizeOf<LVITEMW>() >= 44);
    }
}
