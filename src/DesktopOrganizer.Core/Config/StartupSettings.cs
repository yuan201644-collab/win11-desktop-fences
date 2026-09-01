using System;

namespace DesktopOrganizer.Core.Config;

/// <summary>
/// Logon behaviour for the tray-resident app.
///
/// <see cref="RunAtLogon"/> mirrors a Windows "Run" registry entry — it is the user-facing switch;
/// the registry is only a cache of it, and the app re-syncs it on every launch so a moved exe keeps
/// working.
///
/// <see cref="LogonDelaySeconds"/> exists because the overlay talks to the shell's desktop ListView:
/// right after logon that window frequently does not exist yet for a few seconds. Starting
/// immediately would find no icons at all and, worse, would treat intentionally collapsed icons as
/// stranded. Waiting costs nothing because the app is idle in the tray anyway.
///
/// Pure data, persisted as JSON and unit-tested.
/// </summary>
public sealed record StartupSettings(
    bool RunAtLogon = true,
    int LogonDelaySeconds = 5)
{
    /// <summary>Upper bound for the delay, so a hand-edited file can never stall startup indefinitely.</summary>
    public const int MaxLogonDelaySeconds = 60;

    public static StartupSettings Default => new();

    /// <summary>Clamps the delay into [0, <see cref="MaxLogonDelaySeconds"/>].</summary>
    public StartupSettings Normalized() => this with
    {
        LogonDelaySeconds = Math.Clamp(LogonDelaySeconds, 0, MaxLogonDelaySeconds),
    };
}
