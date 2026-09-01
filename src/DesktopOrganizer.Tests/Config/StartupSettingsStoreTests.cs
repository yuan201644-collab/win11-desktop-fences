using System;
using System.IO;
using DesktopOrganizer.Core.Config;
using Xunit;

namespace DesktopOrganizer.Tests.Config;

public class StartupSettingsStoreTests
{
    private static string TempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DotestStartup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "startup.json");
    }

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var path = TempPath();
        var settings = new StartupSettings(RunAtLogon: false, LogonDelaySeconds: 12);
        StartupSettingsStore.Save(path, settings);
        Assert.Equal(settings, StartupSettingsStore.Load(path));
    }

    [Fact]
    public void Load_MissingFile_DefaultsToAutoStartOn()
    {
        var loaded = StartupSettingsStore.Load(TempPath());
        Assert.True(loaded.RunAtLogon);
        Assert.Equal(5, loaded.LogonDelaySeconds);
    }

    [Fact]
    public void Load_CorruptFile_DefaultsInsteadOfThrowing()
    {
        var path = TempPath();
        File.WriteAllText(path, "{ not json");
        Assert.Equal(StartupSettings.Default, StartupSettingsStore.Load(path));
    }

    [Theory]
    [InlineData(-30, 0)]
    [InlineData(0, 0)]
    [InlineData(37, 37)]
    [InlineData(9999, StartupSettings.MaxLogonDelaySeconds)]
    public void Load_ClampsDelayIntoRange(int written, int expected)
    {
        var path = TempPath();
        StartupSettingsStore.Save(path, new StartupSettings(true, written));
        Assert.Equal(expected, StartupSettingsStore.Load(path).LogonDelaySeconds);
    }

    [Fact]
    public void Normalized_ClampsDirectly()
    {
        Assert.Equal(0, new StartupSettings(true, -5).Normalized().LogonDelaySeconds);
        Assert.Equal(StartupSettings.MaxLogonDelaySeconds,
            new StartupSettings(true, 500).Normalized().LogonDelaySeconds);
    }
}
