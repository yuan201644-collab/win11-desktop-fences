using System;

namespace DesktopOrganizer.Win32;

public sealed class DesktopAutoArrangeException : Exception
{
    public DesktopAutoArrangeException(string message) : base(message) { }
}
