namespace DesktopMediaController.Core;

/// <summary>
/// A rectangle in <b>physical</b> screen pixels. On a multi-monitor desktop the origin is the
/// virtual screen's top-left, so both <see cref="X"/> and <see cref="Y"/> can be negative
/// (this machine has a secondary monitor at x = −2560).
/// </summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    /// <summary>Exclusive right edge.</summary>
    public int Right => X + Width;

    /// <summary>Exclusive bottom edge.</summary>
    public int Bottom => Y + Height;
}
