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
}
