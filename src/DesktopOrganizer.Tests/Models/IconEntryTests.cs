using DesktopOrganizer.Core.Models;
using Xunit;

namespace DesktopOrganizer.Tests.Models;

public class IconEntryTests
{
    [Fact]
    public void DefaultCategoryIsOther()
    {
        var icon = new IconEntry(0, "report.pdf", @"C:\Users\x\Desktop\report.pdf", null);
        Assert.Equal(Category.Other, icon.Category);
    }

    [Fact]
    public void KeepsProvidedCategoryAndValues()
    {
        var icon = new IconEntry(3, "Web.lnk", @"C:\Users\x\Desktop\Web.lnk", "chrome.exe", Category.Browser);
        Assert.Equal(3, icon.Index);
        Assert.Equal("Web.lnk", icon.Name);
        Assert.Equal("chrome.exe", icon.LinkTargetApp);
        Assert.Equal(Category.Browser, icon.Category);
    }
}
