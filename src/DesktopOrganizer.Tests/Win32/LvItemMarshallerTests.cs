using DesktopOrganizer.Win32;
using Xunit;

namespace DesktopOrganizer.Tests.Win32;

public class LvItemMarshallerTests
{
    [Fact]
    public void RoundTripsUnicodeViaLocalProcess()
    {
        var pid = Environment.ProcessId;
        using var m = new LvItemMarshaller(pid);
        Assert.Equal("héllo桌面", m.RoundTripString("héllo桌面"));
    }

    /// <summary>Mirrors what Explorer does with the packed lParam: LOWORD/HIWORD as signed 16-bit.</summary>
    private static (int X, int Y) Unpack(IntPtr lparam)
    {
        long v = lparam.ToInt64();
        return ((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
    }

    // ---------------------------------------------------------------------------------------
    // LVM_SETITEMPOSITION packs coordinates into two 16-bit words. Anything outside
    // [-32768, 32767] was silently truncated: a folder box's 30th parked icon at y=-34352 came
    // back as +31184, which stranded the icon and poisoned the collapsed tab's bounding box —
    // "box and icons vanish together". MakeLParam clamps before packing; these lock that in.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void MakeLParam_InRangeNegative_RoundTripsExactly()
    {
        // -31000 / -32000 sit inside the signed 16-bit range, so they must survive untouched.
        var (x, y) = Unpack(LvItemMarshaller.MakeLParam(-31000, -32000));
        Assert.Equal(-31000, x);
        Assert.Equal(-32000, y);
    }

    [Fact]
    public void MakeLParam_InRangePositive_RoundTripsExactly()
    {
        var (x, y) = Unpack(LvItemMarshaller.MakeLParam(1920, 1080));
        Assert.Equal(1920, x);
        Assert.Equal(1080, y);
    }

    [Fact]
    public void MakeLParam_BelowInt16Floor_ClampsInsteadOfWrapping()
    {
        // The exact coordinates from the log, both past the int16 floor: y=-34352 was truncated to
        // 0x79D0 = +31184 and x=-34128 to 0x7AD0. Both must now clamp instead of turning positive.
        var (x, y) = Unpack(LvItemMarshaller.MakeLParam(-34128, -34352));
        Assert.Equal(short.MinValue, x);
        Assert.Equal(short.MinValue, y); // clamped — never a bogus positive coordinate
        Assert.True(y < 0, "a parked icon must stay on the negative (off-screen) side");
    }

    [Fact]
    public void MakeLParam_AboveInt16Ceiling_ClampsInsteadOfWrapping()
    {
        var (x, y) = Unpack(LvItemMarshaller.MakeLParam(40000, 70000));
        Assert.Equal(short.MaxValue, x);
        Assert.Equal(short.MaxValue, y);
    }

    [Fact]
    public void MakeLParam_NeverProducesABogusPositiveFromANegativePark()
    {
        // Sweep the whole parking range: nothing may flip sign into "looks on-screen" territory.
        for (int y = -40000; y <= -30000; y += 7)
        {
            var (_, unpackedY) = Unpack(LvItemMarshaller.MakeLParam(-32000, y));
            Assert.True(unpackedY < 0, $"y={y} unpacked as {unpackedY} — a sign flip would strand the icon");
            Assert.InRange(unpackedY, short.MinValue, short.MaxValue);
        }
    }
}
