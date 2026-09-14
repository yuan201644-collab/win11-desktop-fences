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
/// The card has exactly one design size, <see cref="Base"/>: the card renders that geometry and
/// scaling happens through the card's layout transform. A size is therefore always a scale preset
/// multiplied out, and <see cref="Coerce"/> snaps whatever it is handed — a drag leftover, an old
/// file's free size — onto the nearest preset. That is what makes the ratio fixed: nothing can
/// express an off-ratio size any more, only a bigger or smaller copy of the same card.
/// </para>
/// </remarks>
public readonly record struct WidgetSize(double WidthDip, double HeightDip)
{
    /// <summary>The one design geometry: 620x216, a 2.87:1 card with a full-height cover column.</summary>
    public const double BaseWidthDip = 620;

    public const double BaseHeightDip = 216;

    /// <summary>The design size. Everything on the card is measured against exactly this.</summary>
    public static readonly WidgetSize Base = new(BaseWidthDip, BaseHeightDip);

    /// <summary>What a card is born at, and what an unusable file falls back to. Same as <see cref="Base"/>.</summary>
    public static readonly WidgetSize Default = Base;

    /// <summary>
    /// The only sizes that exist: the design size and its scaled copies. The presets, not a free
    /// range, are the whole point — a fixed ratio keeps the cover square and the three lyric rows
    /// in the panel at every size.
    /// </summary>
    public static readonly double[] Scales = [0.75, 1.0, 1.25];

    /// <summary>Whether these numbers describe something renderable. Only sanity, not the ratio —
    /// <see cref="Coerce"/> is what enforces the ratio.</summary>
    public bool IsUsable =>
        double.IsFinite(WidthDip) && double.IsFinite(HeightDip) &&
        WidthDip > 0 && HeightDip > 0;

    /// <summary>
    /// The scale preset closest to what the given numbers describe, judged on height first.
    /// </summary>
    /// <remarks>
    /// Height is the reference because the cover column and the lyric block both hang off it; a
    /// missing or nonsensical height falls back to the width, and if neither axis says anything the
    /// default scale wins. A placement.json from the free-resize era (e.g. 440x176) lands on 0.75 —
    /// 176/216 sits between the presets, and the smaller one is the honest reading of "the card was
    /// this small".
    /// </remarks>
    public static double NearestScale(double widthDip, double heightDip)
    {
        double reference;
        if (double.IsFinite(heightDip) && heightDip > 0)
            reference = heightDip / BaseHeightDip;
        else if (double.IsFinite(widthDip) && widthDip > 0)
            reference = widthDip / BaseWidthDip;
        else
            return 1.0;

        var best = Scales[0];
        var bestDistance = Math.Abs(reference - best);
        for (var i = 1; i < Scales.Length; i++)
        {
            var distance = Math.Abs(reference - Scales[i]);
            if (distance < bestDistance)
            {
                best = Scales[i];
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>The design geometry multiplied out to <paramref name="scale"/>.</summary>
    public static WidgetSize ForScale(double scale) =>
        new(Math.Round(BaseWidthDip * scale, 3), Math.Round(BaseHeightDip * scale, 3));

    /// <summary>
    /// Turns whatever was handed in — a drag leftover, an old settings file, garbage — into a size
    /// the card can actually render: the nearest scale preset.
    /// </summary>
    /// <remarks>
    /// A non-positive or non-finite axis means "no opinion", and both fall through to the default
    /// scale rather than being guessed at. Old files that predate the fixed ratio are snapped, not
    /// rejected: their intent ("about this big") survives, their exact ratio does not.
    /// </remarks>
    public static WidgetSize Coerce(double widthDip, double heightDip) =>
        ForScale(NearestScale(widthDip, heightDip));
}
