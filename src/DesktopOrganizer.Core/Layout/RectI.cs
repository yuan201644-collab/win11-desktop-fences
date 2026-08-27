namespace DesktopOrganizer.Core.Layout;

public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Contains(PointI p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

    public RectI Inflate(int dx, int dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);
}
