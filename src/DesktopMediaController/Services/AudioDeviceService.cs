using DesktopMediaController.Core;
using DesktopMediaController.Win32;

namespace DesktopMediaController.Services;

/// <summary>
/// The widget's view of the machine's playback devices: which ones there are, which one is the
/// default, and how to move the default.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately stateless between calls — every question re-enumerates. It costs one COM call round
/// and happens at most once every couple of seconds, whereas a cached list is a list that goes stale
/// the moment a Bluetooth speaker is switched off or the user changes device in the system settings.
/// That is also why there is no endpoint-notification sink: the list is rebuilt when the user opens
/// it, which is the only moment the answer has to be right, and a managed COM callback living on the
/// UI thread is a lifetime problem (unregister before shutdown or the audio service calls into a dead
/// object) for no visible gain.
/// </para>
/// <para>
/// Nothing here throws. A machine whose audio service is unavailable gets an empty list and a
/// disabled-looking menu rather than a widget that dies on the desktop.
/// </para>
/// </remarks>
internal sealed class AudioDeviceService
{
    /// <summary>Every active playback endpoint, the current default marked. Empty if none or unreachable.</summary>
    public IReadOnlyList<AudioDeviceInfo> Devices() => CoreAudioNative.EnumerateRenderDevices();

    /// <summary>The endpoint that is the default output right now, or null when there is none.</summary>
    public AudioDeviceInfo? Current()
    {
        var id = CoreAudioNative.DefaultRenderId();
        if (id is null) return null;

        // Matched against the enumerated list rather than built from the id alone: the friendly name
        // and the kind only exist on the endpoint objects themselves.
        foreach (var device in CoreAudioNative.EnumerateRenderDevices())
        {
            if (string.Equals(device.Id, id, StringComparison.OrdinalIgnoreCase)) return device;
        }
        return null;
    }

    /// <summary>Makes <paramref name="deviceId"/> the system's default output.</summary>
    /// <returns>False when Windows refused — most likely because the endpoint went away between the
    /// list being built and the click landing.</returns>
    public bool SetDefault(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
        return CoreAudioNative.SetDefaultRenderDevice(deviceId);
    }
}
