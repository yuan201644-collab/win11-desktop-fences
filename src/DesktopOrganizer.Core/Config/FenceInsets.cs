namespace DesktopOrganizer.Core.Config;

/// <summary>
/// Extra padding on each edge of a fence box, beyond the grid cell that holds the icons. Lets the
/// user widen the box on one side (e.g. so a long icon name on the leftmost column no longer spills
/// out) without affecting the icons themselves. Pure data, persisted and unit-tested. The default is
/// deliberately small: fat boxes make every cluster bloated, which trips the builder's overlap
/// push-down and scatters the fences — better to start tight and let the user widen as needed.
/// </summary>
public sealed record FenceInsets(int Left, int Right, int Top, int Bottom)
{
    public static FenceInsets Default => new(Left: 18, Right: 8, Top: 4, Bottom: 8);
}