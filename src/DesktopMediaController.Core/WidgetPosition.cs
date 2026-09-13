namespace DesktopMediaController.Core;

/// <summary>
/// Where the controller window's top-left corner sits, in <b>physical</b> screen pixels.
/// </summary>
/// <remarks>
/// Physical pixels — not WPF DIPs — are the storage unit on purpose. This widget lives on a
/// mixed-DPI desktop (1920×1080 @100% + 2560×1600 @125%), and a DIP value would silently mean a
/// different place after a scale change, drifting the window. The screen coordinate space that
/// Win32 uses is already physical, so nothing has to be converted on the way in or out.
/// <para>
/// The window's <i>size</i> is deliberately not stored: it is derived from the card's design size
/// in DIPs times the current monitor scale, so the widget keeps its apparent size when it is
/// dragged onto the other monitor instead of being frozen at whatever scale it was last saved at.
/// </para>
/// </remarks>
public readonly record struct WidgetPosition(int X, int Y)
{
    /// <summary>Margin from the virtual screen's top-left on first run (no saved file yet).</summary>
    public const int FirstRunMargin = 80;

    /// <summary>First-run spot: the top-left of the virtual screen, inset a little.</summary>
    public static WidgetPosition FirstRun(ScreenRect virtualScreen) =>
        new(virtualScreen.X + FirstRunMargin, virtualScreen.Y + FirstRunMargin);
}
