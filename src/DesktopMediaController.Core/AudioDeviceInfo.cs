namespace DesktopMediaController.Core;

/// <summary>
/// One playback (render) endpoint the user can switch the system's default output to.
/// </summary>
/// <param name="Id">The endpoint's Core Audio device id. Stable across reboots, unique per endpoint,
/// and the only thing <c>SetDefaultEndpoint</c> accepts.</param>
/// <param name="Name">Friendly name, cleaned for display by <see cref="AudioDeviceNaming.Clean"/>.</param>
/// <param name="Kind">Which glyph the device list shows beside it.</param>
/// <param name="IsDefault">Whether this endpoint is the system's default output right now.</param>
public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    AudioDeviceKind Kind,
    bool IsDefault);
