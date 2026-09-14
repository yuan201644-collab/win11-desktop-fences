using System.Runtime.InteropServices;
using DesktopMediaController.Core;

namespace DesktopMediaController.Win32;

/// <summary>The direction of an audio endpoint. Only render (playback) is ever asked for.</summary>
internal enum EDataFlow
{
    eRender = 0,
    eCapture = 1,
    eAll = 2,
}

/// <summary>
/// Which of Windows' three default devices an endpoint is the default <i>for</i>.
/// </summary>
/// <remarks>
/// Games and players follow eConsole; eMultimedia is the legacy role a handful of apps still ask for;
/// eCommunications is the "chat" default that a call app picks. Switching output moves the first two
/// and leaves communications alone — a user who switches music to a speaker usually wants their
/// headset to stay the call device.
/// </remarks>
internal enum ERole
{
    eConsole = 0,
    eMultimedia = 1,
    eCommunications = 2,
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr pClient);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr pClient);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint pcDevices);

    [PreserveSig]
    int Item(uint nDevice, out IMMDevice ppDevice);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);

    [PreserveSig]
    int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);

    [PreserveSig]
    int GetId(out IntPtr ppstrId);

    [PreserveSig]
    int GetState(out int pdwState);
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint cProps);

    [PreserveSig]
    int GetAt(uint iProp, out PropertyKey pkey);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant pv);

    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropVariant pv);

    [PreserveSig]
    int Commit();
}

/// <summary>
/// The undocumented policy interface that can make an endpoint the system default.
/// </summary>
/// <remarks>
/// <para>
/// There is no public API for this. Microsoft's own sound control panel, and with it every device
/// switcher on Windows (SoundSwitch, EarTrumpet, AudioSwitcher), goes through
/// <c>CPolicyConfigClient</c>; it has existed since Vista and still exists on Windows 11. It is used
/// here because the alternative is sending keystrokes at the control panel, which is worse in every
/// way that matters.
/// </para>
/// <para>
/// The vtable order below is load-bearing. It matches the Vista interface through
/// <see cref="SetDefaultEndpoint"/>, with the one method Windows 7 appended
/// (<see cref="SetEndpointVisibility"/>) last — so a slot that shifts would leave this code calling
/// the wrong method, not merely failing. Every call is wrapped and its HRESULT checked by
/// <see cref="CoreAudioNative"/>.
/// </para>
/// </remarks>
[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig]
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, out IntPtr ppwfxFormat);

    [PreserveSig]
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, int bDefault, out IntPtr ppwfxFormat);

    [PreserveSig]
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId);

    [PreserveSig]
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, IntPtr pwfxFormat, IntPtr pwfxFormatReq);

    [PreserveSig]
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, int bDefault,
        out long phnsDefaultPeriod, out long phnsMinimumPeriod);

    [PreserveSig]
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, long hnsPeriod);

    [PreserveSig]
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, out IntPtr pMode);

    [PreserveSig]
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, IntPtr mode);

    [PreserveSig]
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, ref PropertyKey key, out PropVariant pv);

    [PreserveSig]
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, ref PropertyKey key, ref PropVariant pv);

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, ERole eRole);

    [PreserveSig]
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, int bVisible);
}

/// <summary>A device property key, as used by <see cref="IPropertyStore"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public int PropertyId;

    public PropertyKey(Guid formatId, int propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }
}

/// <summary>
/// Just enough PROPVARIANT to read a string out of a property store.
/// </summary>
/// <remarks>
/// Declared at its full native size — 24 bytes on x64, 16 on x86 — because the callee writes into the
/// buffer the marshaler allocated for this struct, and a struct declared too small is a stack
/// overwrite, not a truncation. Only the LPWSTR case (vt 31) is ever read.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort Vt;
    private readonly ushort Reserved1;
    private readonly ushort Reserved2;
    private readonly ushort Reserved3;
    public IntPtr Value;
    private readonly IntPtr Reserved4;
}

/// <summary>
/// The controller's window onto Core Audio: which playback endpoints exist, which one is the default,
/// and how to move the default to another one.
/// </summary>
/// <remarks>
/// Every method swallows COM failures and reports them through its return value instead of throwing:
/// the card is a desktop widget, and a machine where the audio service is in a bad state must still
/// get a working music card. Nothing here is called on a hot path — the heaviest use is one
/// enumeration the moment the user opens the device list.
/// </remarks>
internal static class CoreAudioNative
{
    /// <summary>Only endpoints that are plugged in and enabled right now: a Bluetooth speaker that is
    /// switched off must not be offered, since selecting it can only fail.</summary>
    private const int DeviceStateActive = 0x0000_0001;

    private const int STGM_READ = 0x0000_0000;

    private const ushort VT_LPWSTR = 31;

    private const ushort VT_UI4 = 19;

    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    /// <summary>The policy client: the object behind <see cref="IPolicyConfig"/>.</summary>
    private static readonly Guid ClsidPolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    // PKEY_Device_FriendlyName: the name the control panel shows, e.g. "扬声器 (Realtek(R) Audio)".
    private static readonly PropertyKey PKEY_Device_FriendlyName =
        new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    // PKEY_Device_Enumerator: the bus that published the endpoint — "BTHENUM" for Bluetooth. This is
    // how the list knows a device is wireless when its name never says so.
    private static readonly PropertyKey PKEY_Device_Enumerator =
        new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 24);

    // PKEY_AudioEndpoint_FormFactor: the endpoint's shape as the audio stack sees it. Far more
    // reliable than the name — a monitor's HDMI input arrives as "NVIDIA High Definition Audio".
    private static readonly PropertyKey PKEY_AudioEndpoint_FormFactor =
        new(new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), 0);

    /// <summary>
    /// Enumerates every active playback endpoint, marking the one that is the default right now.
    /// </summary>
    /// <returns>Empty when Core Audio cannot be reached — never null, never throws.</returns>
    internal static IReadOnlyList<AudioDeviceInfo> EnumerateRenderDevices()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;

        try
        {
            enumerator = Create<IMMDeviceEnumerator>(ClsidMMDeviceEnumerator);
            if (enumerator is null) return Array.Empty<AudioDeviceInfo>();

            var defaultId = DefaultIdOf(enumerator);

            if (enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceStateActive, out collection) < 0 ||
                collection is null)
            {
                return Array.Empty<AudioDeviceInfo>();
            }

            collection.GetCount(out var count);
            var devices = new List<AudioDeviceInfo>((int)count);

            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) < 0 || device is null) continue;

                var id = IdOf(device);
                if (id is null) continue;

                var friendly = ReadString(device, PKEY_Device_FriendlyName);
                var bus = ReadString(device, PKEY_Device_Enumerator);

                devices.Add(new AudioDeviceInfo(
                    Id: id,
                    Name: AudioDeviceNaming.TrimForDisplay(AudioDeviceNaming.Clean(friendly)),
                    Kind: AudioDeviceNaming.Classify(friendly, bus, ReadFormFactor(device)),
                    IsDefault: string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));

                Marshal.ReleaseComObject(device);
            }

            return devices;
        }
        catch (Exception)
        {
            return Array.Empty<AudioDeviceInfo>();
        }
        finally
        {
            if (collection is not null) Marshal.ReleaseComObject(collection);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>The id of the endpoint that is the default output for <see cref="ERole.eConsole"/>.</summary>
    internal static string? DefaultRenderId()
    {
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = Create<IMMDeviceEnumerator>(ClsidMMDeviceEnumerator);
            return enumerator is null ? null : DefaultIdOf(enumerator);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// Makes <paramref name="deviceId"/> the system's default output, for the console and multimedia
    /// roles. See <see cref="ERole"/> for why the communications default is left where it is.
    /// </summary>
    internal static bool SetDefaultRenderDevice(string deviceId)
    {
        IPolicyConfig? policy = null;
        try
        {
            policy = Create<IPolicyConfig>(ClsidPolicyConfigClient);
            if (policy is null) return false;

            var console = policy.SetDefaultEndpoint(deviceId, ERole.eConsole);
            var multimedia = policy.SetDefaultEndpoint(deviceId, ERole.eMultimedia);

            // One role landing is enough for a player to move; demanding both would report failure to
            // a user whose music has already switched.
            return console >= 0 || multimedia >= 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (policy is not null) Marshal.ReleaseComObject(policy);
        }
    }

    private static string? DefaultIdOf(IMMDeviceEnumerator enumerator)
    {
        IMMDevice? device = null;
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out device) < 0 ||
                device is null)
            {
                return null;
            }
            return IdOf(device);
        }
        finally
        {
            if (device is not null) Marshal.ReleaseComObject(device);
        }
    }

    private static string? IdOf(IMMDevice device)
    {
        if (device.GetId(out var pointer) < 0 || pointer == IntPtr.Zero) return null;

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static string? ReadString(IMMDevice device, PropertyKey key)
    {
        IPropertyStore? store = null;
        try
        {
            if (device.OpenPropertyStore(STGM_READ, out store) < 0 || store is null) return null;

            var variant = new PropVariant();
            if (store.GetValue(ref key, out variant) < 0) return null;

            try
            {
                return variant.Vt == VT_LPWSTR && variant.Value != IntPtr.Zero
                    ? Marshal.PtrToStringUni(variant.Value)
                    : null;
            }
            finally
            {
                PropVariantClear(ref variant);
            }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (store is not null) Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>The endpoint's shape: 1 speakers, 3 headphones, 5 headset, 9 a digital display. −1 when
    /// the property is missing — which it is on some virtual devices, and that is fine.</summary>
    private static int ReadFormFactor(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            if (device.OpenPropertyStore(STGM_READ, out store) < 0 || store is null) return -1;

            var key = PKEY_AudioEndpoint_FormFactor;
            if (store.GetValue(ref key, out var variant) < 0) return -1;

            try
            {
                return variant.Vt == VT_UI4 ? (int)(variant.Value.ToInt64() & 0xFFFF_FFFF) : -1;
            }
            finally
            {
                PropVariantClear(ref variant);
            }
        }
        catch (Exception)
        {
            return -1;
        }
        finally
        {
            if (store is not null) Marshal.ReleaseComObject(store);
        }
    }

    private static T? Create<T>(Guid clsid) where T : class
    {
        var type = Type.GetTypeFromCLSID(clsid);
        return type is null ? null : Activator.CreateInstance(type) as T;
    }

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);
}
