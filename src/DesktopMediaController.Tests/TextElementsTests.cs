using DesktopMediaController.Core;

namespace DesktopMediaController.Tests;

/// <summary>
/// The karaoke renderer builds one inline per character and afterwards colours them by index, so
/// "character" has to mean what a reader sees. These tests pin that down, because getting it wrong is
/// silent: the highlight simply drifts one place out for the rest of any line containing an emoji.
/// </summary>
public sealed class TextElementsTests
{
    /// <summary>
    /// One musical note. Written as an escape rather than as the glyph so the test does not depend on
    /// this file's encoding — and so the value is unmistakable.
    /// </summary>
    private const string Note = "\U0001F3B5";

    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("abc", 3)]
    [InlineData("晴天", 2)]
    public void Count_PlainText_CountsOnePerCharacter(string text, int expected)
    {
        Assert.Equal(expected, TextElements.Count(text));
    }

    [Fact]
    public void Count_ASurrogatePair_IsOneCharacterNotTwo()
    {
        Assert.Equal(2, Note.Length);
        Assert.Equal(1, TextElements.Count(Note));
    }

    [Fact]
    public void Count_ACombiningMark_StaysWithItsBaseCharacter()
    {
        const string composed = "e\u0301";        // e + COMBINING ACUTE ACCENT

        Assert.Equal(2, composed.Length);
        Assert.Equal(1, TextElements.Count(composed));
    }

    [Fact]
    public void Split_ReturnsOneStringPerCharacterAndLosesNothingOnTheWay()
    {
        var text = "晴" + Note + "天 e\u0301";

        var elements = TextElements.Split(text);

        // Reassembly is the property the renderer depends on: the inlines it builds must reproduce the
        // line exactly, or a line would render with characters missing from the middle.
        Assert.Equal(text, string.Concat(elements));
        Assert.Equal(TextElements.Count(text), elements.Length);
        Assert.Contains(Note, elements);
        Assert.Contains("e\u0301", elements);
    }

    [Fact]
    public void Split_EmptyOrNull_IsEmptyRatherThanOneEmptyElement()
    {
        // An empty line must produce no inlines at all, not one that would sit there as a stray index.
        Assert.Empty(TextElements.Split(null));
        Assert.Empty(TextElements.Split(string.Empty));
    }

    [Fact]
    public void CountAt_ReportsHowManyCharactersBeginBeforeEachBoundary()
    {
        var text = "晴" + Note + "天";

        // Disjoint buckets: everything before the boundary, then everything inside it.
        Assert.Equal((0, 1), TextElements.CountAt(text, 0, 1));
        Assert.Equal((1, 1), TextElements.CountAt(text, 1, 3));
        Assert.Equal((3, 0), TextElements.CountAt(text, 4, 4));
    }

    [Fact]
    public void CountAt_ABoundaryInsideASurrogatePair_AttributesTheCharacterWhole()
    {
        // Only reachable when a provider disagrees with itself about its own text. Classifying each
        // character by where it starts keeps the answer monotonic, so one impossible boundary cannot
        // shift every highlight after it down the line.
        var (before, within) = TextElements.CountAt(Note + "天", 1, 2);

        Assert.Equal(1, before);
        Assert.Equal(0, within);
    }
}
