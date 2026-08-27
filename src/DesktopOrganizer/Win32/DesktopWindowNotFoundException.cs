using System;

namespace DesktopOrganizer.Win32;

public sealed class DesktopWindowNotFoundException : Exception
{
    public DesktopWindowNotFoundException(string message) : base(message) { }
}
