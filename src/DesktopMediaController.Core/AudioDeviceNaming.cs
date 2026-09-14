using System.Text.RegularExpressions;

namespace DesktopMediaController.Core;

/// <summary>
/// Turns Core Audio's raw friendly names into something short enough for a 220-pixel flyout, and
/// guesses what each endpoint is for the glyph beside it.
/// </summary>
/// <remarks>
/// Pure string work, deliberately: the audio stack itself is only reachable through COM, so keeping
/// this part away from it is what makes it testable without a sound card.
/// </remarks>
public static partial class AudioDeviceNaming
{
    /// <summary>Longest name the flyout will show before it ellipsises.</summary>
    public const int MaxNameLength = 34;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// Cleans one friendly name: trademark noise out, whitespace collapsed.
    /// </summary>
    /// <remarks>
    /// The prefix is kept on purpose even when the name also carries a driver in parentheses —
    /// "扬声器 (Realtek Audio)" and "耳机 (Realtek Audio)" are two different jacks on the same chip,
    /// and dropping the leading word would make them look identical in the list.
    /// </remarks>
    public static string Clean(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return string.Empty;

        var name = Whitespace().Replace(friendlyName.Replace('\u3000', ' '), " ").Trim();

        // "Realtek(R) Audio" -> "Realtek Audio". Done before the space tidy-up so the removal cannot
        // leave a double space behind.
        name = name
            .Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(C)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("\u00AE", string.Empty)
            .Replace("\u2122", string.Empty);

        name = name.Replace("( ", "(").Replace(" )", ")");
        return Whitespace().Replace(name, " ").Trim();
    }

    /// <summary>
    /// Trims a cleaned name to <see cref="MaxNameLength"/>, appending an ellipsis when it was cut.
    /// </summary>
    /// <remarks>
    /// Counted in text elements, not UTF-16 units: a device named with an emoji must not end up
    /// showing half a surrogate pair.
    /// </remarks>
    public static string TrimForDisplay(string name)
    {
        if (name.Length == 0) return name;

        var elements = TextElements.Split(name);
        return elements.Length <= MaxNameLength ? name : string.Concat(elements[..MaxNameLength]) + "…";
    }

    /// <summary>
    /// Guesses what kind of endpoint this is, from the bus it hangs off, its shape, and its name.
    /// </summary>
    /// <param name="friendlyName">The raw friendly name, cleaned or not.</param>
    /// <param name="enumerator">
    /// The Core Audio bus that published the endpoint — "BTHENUM" for Bluetooth, "HDAUDIO" for the
    /// onboard codec, "USB" for a dongle. Empty when the property could not be read.
    /// </param>
    /// <param name="formFactor">
    /// The endpoint's <c>EndpointFormFactor</c>: 1 speakers, 3 headphones, 5 headset, 8 S/PDIF,
    /// 9 a digital display (HDMI / DisplayPort). Negative when unreadable.
    /// </param>
    /// <remarks>
    /// <para>
    /// The bus wins over everything: it is a fact and the name is marketing. A Bluetooth speaker is
    /// often called just "扬声器 (方糖R(74:B4))", with nothing in the string to say it is wireless.
    /// </para>
    /// <para>
    /// The form factor comes second and settles what names cannot: a monitor's audio input is
    /// "25M2 (NVIDIA High Definition Audio)" here — indistinguishable from a sound card by its name,
    /// but unmistakably a display to the audio stack.
    /// </para>
    /// </remarks>
    public static AudioDeviceKind Classify(string? friendlyName, string? enumerator = null, int formFactor = -1)
    {
        if (!string.IsNullOrEmpty(enumerator) &&
            enumerator.Contains("BTH", StringComparison.OrdinalIgnoreCase))
        {
            return AudioDeviceKind.Bluetooth;
        }

        var name = friendlyName ?? string.Empty;
        if (ContainsAny(name, "蓝牙", "bluetooth")) return AudioDeviceKind.Bluetooth;

        return formFactor switch
        {
            9 => AudioDeviceKind.Monitor,      // a digital display device: HDMI / DisplayPort audio
            3 or 5 => AudioDeviceKind.Headphone, // headphones / headset
            1 => AudioDeviceKind.Speaker,      // speakers
            _ => ByName(name),
        };
    }

    private static AudioDeviceKind ByName(string name)
    {
        if (ContainsAny(name, "显示器", "monitor", "hdmi", "displayport", "dp音频")) return AudioDeviceKind.Monitor;
        if (ContainsAny(name, "耳机", "耳麦", "headphone", "headset")) return AudioDeviceKind.Headphone;
        return AudioDeviceKind.Speaker;
    }

    private static bool ContainsAny(string name, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (name.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
