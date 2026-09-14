namespace DesktopMediaController.Core;

/// <summary>
/// How big the card is, in device-independent pixels.
/// </summary>
/// <remarks>
/// <para>
/// <b>DIPs, unlike the position.</b> Position is stored in physical pixels because a DIP value would
/// mean somewhere else after a monitor scale change; size is the opposite, because the window's
/// physical size is <i>derived</i> — WPF computes it from the card's design size times the monitor's
/// scale. Storing the derived quantity would double-apply the scale on the next launch.
/// </para>
/// <para>
/// The bounds are what the card can actually render rather than a matter of taste. Below the minimum,
/// three lyric lines plus the cover art stop fitting and the layout collapses into overlap; above the
/// maximum, the card stops looking like a card.
/// </para>
/// </remarks>
public readonly record struct WidgetSize(double WidthDip, double HeightDip)
{
    /// <summary>Narrower than this and the transport row and cover start colliding.</summary>
    public const double MinWidthDip = 320;

    /// <summary>Shorter than this and three lyric lines no longer fit.</summary>
    public const double MinHeightDip = 140;

    public const double MaxWidthDip = 1600;

    public const double MaxHeightDip = 900;

    /// <summary>
    /// The size the card is born at. Larger than the pre-lyrics card (360×112) because three lyric
    /// lines need the room, and because a card that is resizable anyway may as well start usable.
    /// </summary>
    public static readonly WidgetSize Default = new(440, 176);

    /// <summary>
    /// Whether these numbers describe a size the card can actually be. False for a missing size — which
    /// is exactly what an older <c>placement.json</c>, written before the card was resizable, contains.
    /// </summary>
    public bool IsUsable =>
        double.IsFinite(WidthDip) && double.IsFinite(HeightDip) &&
        WidthDip >= MinWidthDip && HeightDip >= MinHeightDip;

    /// <summary>
    /// Turns whatever a drag produced — or whatever an old settings file contained — into a size the
    /// card can render.
    /// </summary>
    /// <remarks>
    /// A non-positive or non-finite value means "not specified", and falls back to
    /// <see cref="Default"/> rather than being clamped up to the minimum: an old file has no opinion
    /// about size, whereas a drag that ran into the limit has a very definite one. Each axis is judged
    /// on its own, so a file that specified only one of them keeps the one it did specify.
    /// </remarks>
    public static WidgetSize Coerce(double widthDip, double heightDip) => new(
        Axis(widthDip, MinWidthDip, MaxWidthDip, Default.WidthDip),
        Axis(heightDip, MinHeightDip, MaxHeightDip, Default.HeightDip));

    private static double Axis(double value, double min, double max, double fallback) =>
        !double.IsFinite(value) || value <= 0 ? fallback : Math.Clamp(value, min, max);
}
