namespace DesktopMediaController.Core;

/// <summary>
/// How much the controller trusts a media session's source to be something the user wants on the card.
/// </summary>
/// <remarks>
/// Three tiers rather than a simple on/off switch, because the useful answer for a browser is neither
/// "yes" nor "no": Chrome can be carrying a song or a video and SMTC cannot tell them apart, so it is
/// allowed to fill the card but never allowed to take it from a real music player.
/// </remarks>
public enum SourceTier
{
    /// <summary>
    /// Never chosen automatically — 抖音, a video player, or any app nobody has vouched for. Still
    /// reachable by cycling the source label by hand.
    /// </summary>
    Excluded = 0,

    /// <summary>Chosen only when nothing at a higher tier is playing. Browsers live here.</summary>
    Fallback = 1,

    /// <summary>The music players themselves. These win the card while they are playing.</summary>
    Preferred = 2,
}
