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
/// This type is only the <i>position</i> half of the card's placement; the size half lives in
/// <see cref="WidgetSize"/> and is in DIPs for the opposite reason. See
/// <see cref="WidgetPlacement"/> for both together.
/// </para>
/// </remarks>
public readonly record struct WidgetPosition(int X, int Y);
