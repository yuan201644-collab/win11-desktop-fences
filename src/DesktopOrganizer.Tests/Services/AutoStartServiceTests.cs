using System;
using System.Collections.Generic;
using System.IO;
using DesktopOrganizer.Services;
using Xunit;

namespace DesktopOrganizer.Tests.Services;

/// <summary>
/// Only the pure helpers of <see cref="AutoStartService"/> are covered here. The registry calls are
/// deliberately untested: writing HKCU\...\Run from a test run would mutate the developer's real
/// login configuration (and machine policy can make it fail), which is exactly the kind of test that
/// is worse than no test.
/// </summary>
public class AutoStartServiceTests
{
    [Fact]
    public void BuildCommand_QuotesPathAndAddsStartupFlag()
    {
        var cmd = AutoStartService.BuildCommand(@"C:\Program Files\Desktop Organizer\DesktopOrganizer.exe");
        Assert.Equal(@"""C:\Program Files\Desktop Organizer\DesktopOrganizer.exe"" --startup", cmd);
    }

    [Theory]
    [InlineData("--startup")]
    [InlineData("/startup")]
    [InlineData("--STARTUP")]
    public void HasStartupArg_RecognisesTheFlagInAnyPosition(string arg)
    {
        Assert.True(AutoStartService.HasStartupArg(new[] { arg }));
        Assert.True(AutoStartService.HasStartupArg(new[] { "other", arg }));
    }

    [Fact]
    public void HasStartupArg_IgnoresUnrelatedArguments()
    {
        Assert.False(AutoStartService.HasStartupArg(Array.Empty<string>()));
        Assert.False(AutoStartService.HasStartupArg(new[] { "--start", "startup", "-startup" }));
    }

    [Fact]
    public void CurrentExePath_PointsAtTheRunningTestHost()
    {
        var path = AutoStartService.CurrentExePath();
        Assert.False(string.IsNullOrEmpty(path));
        // Whatever hosts the test run, it must be a real file on disk — the Run key stores this verbatim.
        Assert.True(System.IO.File.Exists(path), $"exe path not found: {path}");
    }

    [Fact]
    public void BuildCommand_RoundTripsThroughTheDelayAwareStartupPath()
    {
        var cmd = AutoStartService.BuildCommand("D:\\tools\\DO.exe");
        var args = cmd.Split('"')[2].Trim().Split(' ');
        Assert.True(AutoStartService.HasStartupArg(new List<string>(args)));
    }

    [Fact]
    public void ResolveTargetExe_PrefersReleaseSiblingOverRunCopy()
    {
        // repo\run\DesktopOrganizer.exe is the dev build; the stable release copy sits beside it.
        var root = Path.Combine(Path.GetTempPath(), "DO_ResolveTest", Guid.NewGuid().ToString("N"));
        var runDir = Path.Combine(root, "run");
        var releaseDir = Path.Combine(root, "release");
        Directory.CreateDirectory(runDir);
        Directory.CreateDirectory(releaseDir);
        var releaseExe = Path.Combine(releaseDir, "DesktopOrganizer.exe");
        File.WriteAllText(releaseExe, "stub");

        var runExe = Path.Combine(runDir, "DesktopOrganizer.exe");
        Assert.Equal(releaseExe, AutoStartService.ResolveTargetExe(runExe));
    }

    [Fact]
    public void ResolveTargetExe_FallsBackToRunningExeWhenReleaseMissing()
    {
        // A portable copy with no release sibling must register itself, not a phantom path.
        var root = Path.Combine(Path.GetTempPath(), "DO_ResolveTest2", Guid.NewGuid().ToString("N"));
        var runDir = Path.Combine(root, "run");
        Directory.CreateDirectory(runDir); // no release sibling

        var runExe = Path.Combine(runDir, "DesktopOrganizer.exe");
        Assert.Equal(runExe, AutoStartService.ResolveTargetExe(runExe));
    }
}
