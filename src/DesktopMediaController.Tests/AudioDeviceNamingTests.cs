using System.Globalization;
using DesktopMediaController.Core;

namespace DesktopMediaController.Tests;

/// <summary>
/// Device naming and classification: the part of the audio layer that is arithmetic on strings, and
/// therefore the part a test can pin down. The COM half is exercised by hand — enumerating real
/// endpoints in a unit test would make the suite depend on the machine's sound card.
/// </summary>
public class AudioDeviceNamingTests
{
    [Fact]
    public void Clean_DropsTrademarkNoiseButKeepsTheJack()
    {
        // The leading word is the only thing that separates two jacks on one chip: "扬声器 (Realtek
        // Audio)" and "耳机 (Realtek Audio)" are different sockets.
        Assert.Equal("扬声器 (Realtek Audio)", AudioDeviceNaming.Clean("扬声器 (Realtek(R) Audio)"));
        Assert.Equal("耳机 (Realtek Audio)", AudioDeviceNaming.Clean("耳机 (Realtek(R) Audio)"));
    }

    [Theory]
    [InlineData("扬声器  (Realtek  Audio)", "扬声器 (Realtek Audio)")] // collapsed whitespace
    [InlineData("Mi Speaker™", "Mi Speaker")]                        // trademark glyph
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Clean_TidiesWhitespaceAndTrademarks(string? input, string expected) =>
        Assert.Equal(expected, AudioDeviceNaming.Clean(input));

    [Fact]
    public void TrimForDisplay_CutsOnTextElementsNotCodeUnits()
    {
        var longName = new string('a', 40);
        var trimmed = AudioDeviceNaming.TrimForDisplay(longName);

        Assert.EndsWith("…", trimmed);
        Assert.Equal(AudioDeviceNaming.MaxNameLength + 1, trimmed.Length);

        // A name at the limit is untouched — no ellipsis is added "just in case".
        var atLimit = new string('a', AudioDeviceNaming.MaxNameLength);
        Assert.Equal(atLimit, AudioDeviceNaming.TrimForDisplay(atLimit));
    }

    [Fact]
    public void TrimForDisplay_NeverSplitsASurrogatePair()
    {
        // One past the limit, so it is actually trimmed: 34 emoji are kept and one ellipsis added.
        var emoji = string.Concat(Enumerable.Repeat("\U0001F3B5", AudioDeviceNaming.MaxNameLength + 2));
        var trimmed = AudioDeviceNaming.TrimForDisplay(emoji);

        // Measured in text elements: 34 emoji plus the ellipsis. In UTF-16 units the same string is
        // 69 long, which is exactly why the trim cannot be done with a substring.
        Assert.Equal(AudioDeviceNaming.MaxNameLength + 1, new StringInfo(trimmed).LengthInTextElements);
        Assert.True(char.IsSurrogate(trimmed[^2]));
    }

    [Fact]
    public void Classify_TrustsTheBusOverTheName()
    {
        // A Bluetooth speaker whose name says nothing about being wireless — detected by its bus.
        Assert.Equal(
            AudioDeviceKind.Bluetooth,
            AudioDeviceNaming.Classify("扬声器 (方糖R(74:B4))", enumerator: "BTHENUM", formFactor: 1));
    }

    [Theory]
    [InlineData(9, AudioDeviceKind.Monitor)]   // a digital display device: HDMI / DP audio
    [InlineData(3, AudioDeviceKind.Headphone)] // headphones
    [InlineData(5, AudioDeviceKind.Headphone)] // headset
    [InlineData(1, AudioDeviceKind.Speaker)]   // speakers
    [InlineData(8, AudioDeviceKind.Speaker)]   // S/PDIF: no glyph of its own, speakers is closest
    public void Classify_ReadsTheFormFactor(int formFactor, AudioDeviceKind expected) =>
        Assert.Equal(expected, AudioDeviceNaming.Classify("25M2 (NVIDIA High Definition Audio)", "HDAUDIO", formFactor));

    [Fact]
    public void Classify_FallsBackToTheNameWhenTheFormFactorIsMissing()
    {
        Assert.Equal(AudioDeviceKind.Monitor, AudioDeviceNaming.Classify("显示器 (AMD High Definition Audio)"));
        Assert.Equal(AudioDeviceKind.Headphone, AudioDeviceNaming.Classify("耳机 (ZhuAudio ZG3)"));
        Assert.Equal(AudioDeviceKind.Speaker, AudioDeviceNaming.Classify("扬声器 (Steam Streaming Speakers)"));
        Assert.Equal(AudioDeviceKind.Bluetooth, AudioDeviceNaming.Classify("耳机 (WH-1000XM5 蓝牙)"));
    }
}
