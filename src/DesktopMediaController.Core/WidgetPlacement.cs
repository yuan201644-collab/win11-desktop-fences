namespace DesktopMediaController.Core;

/// <summary>
/// Everything about where and how big the card is: a position in physical pixels and a size in DIPs.
/// </summary>
/// <remarks>
/// The two halves are stored in different units on purpose, and the reason is not symmetry — it is that
/// the two quantities have different owners. The window's position is set by us, in the physical
/// coordinate space Win32 already uses. Its size is <i>derived</i> by WPF from the card's design size
/// and the monitor's scale factor, so storing the derived value would apply the scale twice.
/// </remarks>
/// <param name="X">Left edge, in physical pixels, in the virtual screen's coordinate space (may be negative).</param>
/// <param name="Y">Top edge, in physical pixels.</param>
/// <param name="WidthDip">Card width in device-independent pixels.</param>
/// <param name="HeightDip">Card height in device-independent pixels.</param>
/// <param name="Pinned">
/// Whether the user asked for the card to float above every window. The default — and therefore what
/// a placement file written before the pin existed, or a first run, resolves to — is <c>false</c>: the
/// card lives in the normal window order, under whatever software the user is working in, and the pin
/// button is what lifts it out.
/// </param>
public readonly record struct WidgetPlacement(
    int X, int Y, double WidthDip, double HeightDip, bool Pinned = false)
{
    /// <summary>Margin from the virtual screen's top-left when there is no saved placement.</summary>
    public const int FirstRunMargin = 80;

    /// <summary>First-run spot: the virtual screen's top-left, inset a little, at the default size.</summary>
    public static WidgetPlacement FirstRun(ScreenRect virtualScreen) => new(
        virtualScreen.X + FirstRunMargin,
        virtualScreen.Y + FirstRunMargin,
        WidgetSize.Default.WidthDip,
        WidgetSize.Default.HeightDip);

    public WidgetPosition Position => new(X, Y);

    public WidgetSize Size => new(WidthDip, HeightDip);

    public WidgetPlacement WithPosition(WidgetPosition position) => this with { X = position.X, Y = position.Y };

    public WidgetPlacement WithSize(WidgetSize size) =>
        this with { WidthDip = size.WidthDip, HeightDip = size.HeightDip };
}
