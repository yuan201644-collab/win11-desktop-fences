namespace DesktopMediaController.Core;

/// <summary>
/// What a render endpoint looks like, for the glyph shown beside it in the device list.
/// </summary>
/// <remarks>
/// A guess, not a fact: the audio stack publishes no "form factor" for an endpoint, so this comes from
/// the device's bus and its friendly name (see <see cref="AudioDeviceNaming.Classify"/>). It drives an
/// icon and nothing else — a wrong guess costs a wrong picture, never a wrong switch.
/// </remarks>
public enum AudioDeviceKind
{
    /// <summary>Plain speakers: laptop audio, line out, a desktop's rear jack.</summary>
    Speaker,

    /// <summary>Wired headphones or a headset.</summary>
    Headphone,

    /// <summary>Any endpoint on the Bluetooth bus — headphones, speakers, a car kit.</summary>
    Bluetooth,

    /// <summary>A monitor's audio input (HDMI / DisplayPort), i.e. the sound coming out of a display.</summary>
    Monitor,
}
