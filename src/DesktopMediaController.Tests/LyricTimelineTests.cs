using DesktopMediaController.Core;

namespace DesktopMediaController.Tests;

public sealed class LyricTimelineTests
{
    /// <summary>
    /// A document shaped like real QRC output: two syllable-timed lines, then a line with no syllables
    /// at all — which is exactly what <c>MixedSynced</c> looks like, and the case that a per-document
    /// decision would get wrong.
    /// </summary>
    private static readonly LyricLine[] Document =
    {
        Built(0, ("晴", 0, 160), ("天", 160, 320)),
        Built(2_000, ("词", 2_000, 2_472), ("：", 2_472, 2_836), ("周", 2_836, 3_308)),
        new(4_000, 6_000, "（间奏，无逐字）"),
    };

    private static LyricLine Built(int startMs, params (string Text, int StartMs, int EndMs)[] syllables)
    {
        var parts = syllables.Select(s => new LyricSyllable(s.Text, s.StartMs, s.EndMs)).ToArray();
        return new LyricLine(startMs, parts[^1].EndMs, string.Concat(parts.Select(p => p.Text)), parts);
    }

    // ---- locating the line ----------------------------------------------------------------------

    [Fact]
    public void LineIndexAt_BeforeTheFirstLine_IsMinusOneSoNothingIsHighlightedDuringTheIntro()
    {
        Assert.Equal(-1, LyricTimeline.LineIndexAt(Document, -1));
    }

    [Fact]
    public void LineIndexAt_ExactlyOnALinesStart_SelectsThatLine()
    {
        Assert.Equal(0, LyricTimeline.LineIndexAt(Document, 0));
        Assert.Equal(1, LyricTimeline.LineIndexAt(Document, 2_000));
        Assert.Equal(2, LyricTimeline.LineIndexAt(Document, 4_000));
    }

    [Fact]
    public void LineIndexAt_BetweenLines_KeepsTheEarlierOneHighlighted()
    {
        Assert.Equal(0, LyricTimeline.LineIndexAt(Document, 1_999));
        Assert.Equal(1, LyricTimeline.LineIndexAt(Document, 3_999));
    }

    [Fact]
    public void LineIndexAt_PastTheEnd_PinsToTheLastLineRatherThanRunningOff()
    {
        Assert.Equal(2, LyricTimeline.LineIndexAt(Document, 9_999_999));
    }

    [Fact]
    public void LineIndexAt_AnEmptyDocument_IsMinusOne()
    {
        Assert.Equal(-1, LyricTimeline.LineIndexAt(Array.Empty<LyricLine>(), 1_000));
    }

    // ---- locating the syllable ------------------------------------------------------------------

    [Fact]
    public void SungSyllableCountAt_TheInstantALineStarts_CountsItsFirstSyllableAsBegun()
    {
        // "Begun", not "finished": the first syllable owns the moment the line starts, so the line
        // must not spend its first 160 ms looking unsung.
        Assert.Equal(1, LyricTimeline.SungSyllableCountAt(Document[0], 0));
    }

    [Fact]
    public void SungSyllableCountAt_MidLine_CountsEverySyllableThatHasStarted()
    {
        Assert.Equal(1, LyricTimeline.SungSyllableCountAt(Document[0], 159));
        Assert.Equal(2, LyricTimeline.SungSyllableCountAt(Document[0], 160));
        Assert.Equal(3, LyricTimeline.SungSyllableCountAt(Document[1], 2_836));
    }

    [Fact]
    public void SungSyllableCountAt_PastTheLastSyllable_StopsAtTheTotal()
    {
        Assert.Equal(2, LyricTimeline.SungSyllableCountAt(Document[0], 999_999));
    }

    [Fact]
    public void SungSyllableCountAt_ALineWithoutSyllables_IsAlwaysZero()
    {
        Assert.Equal(0, LyricTimeline.SungSyllableCountAt(Document[2], 5_000));
    }

    [Fact]
    public void Locate_ComposesBothIndices()
    {
        Assert.Equal(LyricCursor.BeforeFirstLine, LyricTimeline.Locate(Document, -1));

        var cursor = LyricTimeline.Locate(Document, 2_900);
        Assert.Equal(1, cursor.LineIndex);
        Assert.Equal(3, cursor.SungSyllableCount);
        Assert.True(cursor.HasLine);
    }

    // ---- splitting the line for the renderer ----------------------------------------------------

    [Fact]
    public void Split_ALineWithNoSyllables_PutsTheWholeLineInCurrent()
    {
        // This is the "highlight the whole line" degradation, expressed in the same three fields as
        // the karaoke case so the card needs only one code path.
        var paint = LyricTimeline.Split(Document[2], new LyricCursor(2, 0));

        Assert.Equal(string.Empty, paint.Sung);
        Assert.Equal("（间奏，无逐字）", paint.Current);
        Assert.Equal(string.Empty, paint.Remaining);
    }

    [Fact]
    public void Split_BeforeAnySyllableOfALine_LeavesEverythingAhead()
    {
        var paint = LyricTimeline.Split(Document[0], new LyricCursor(0, 0));

        Assert.Equal(string.Empty, paint.Sung);
        Assert.Equal(string.Empty, paint.Current);
        Assert.Equal("晴天", paint.Remaining);
    }

    [Fact]
    public void Split_OnTheFirstSyllable_HighlightsItAndLeavesTheRestAhead()
    {
        var paint = LyricTimeline.Split(Document[0], new LyricCursor(0, 1));

        Assert.Equal(string.Empty, paint.Sung);
        Assert.Equal("晴", paint.Current);
        Assert.Equal("天", paint.Remaining);
    }

    [Fact]
    public void Split_OnTheLastSyllable_SwallowsTheRest()
    {
        var paint = LyricTimeline.Split(Document[1], new LyricCursor(1, 3));

        Assert.Equal("词：", paint.Sung);
        Assert.Equal("周", paint.Current);
        Assert.Equal(string.Empty, paint.Remaining);
        Assert.True(paint.IsFullySung);
    }

    [Fact]
    public void Split_TheThreePiecesAlwaysReconstructTheLine()
    {
        // The card paints the pieces as three adjacent runs, so any gap or duplication between them
        // would show up as a visible seam in the middle of a line.
        for (var line = 0; line < Document.Length; line++)
        {
            var source = Document[line];
            for (var count = 0; count <= source.Syllables.Count; count++)
            {
                var paint = LyricTimeline.Split(source, new LyricCursor(line, count));
                Assert.Equal(source.Text, paint.Sung + paint.Current + paint.Remaining);
            }
        }
    }

    [Fact]
    public void Split_PreservesWhitespaceSyllablesSoLatinLyricsKeepTheirWordBreaks()
    {
        // QRC publishes the space between two English words as its own syllable. Dropping or
        // normalising it would run the words together.
        var line = Built(0, ("Hel", 0, 200), ("lo", 200, 400), (" ", 400, 450), ("world", 450, 900));

        var paint = LyricTimeline.Split(line, new LyricCursor(0, 3));

        Assert.Equal("Hello", paint.Sung);
        Assert.Equal(" ", paint.Current);
        Assert.Equal("world", paint.Remaining);
    }

    [Fact]
    public void Split_ASingleSyllableLine_IsCurrentFromTheStart()
    {
        var line = Built(0, ("啊", 0, 500));

        var paint = LyricTimeline.Split(line, new LyricCursor(0, 1));

        Assert.Equal(string.Empty, paint.Sung);
        Assert.Equal("啊", paint.Current);
        Assert.Equal(string.Empty, paint.Remaining);
    }

    [Fact]
    public void Split_WhenTheTextDisagreesWithTheSyllables_ClampsInsteadOfThrowing()
    {
        // Nothing should produce this, but it arrives from the network and runs inside a paint
        // callback, so a provider contradicting itself must not be able to take the widget down.
        var inconsistent = new LyricLine(
            0, 1_000, "AB",
            new[] { new LyricSyllable("ABCDE", 0, 1_000) });

        var paint = LyricTimeline.Split(inconsistent, new LyricCursor(0, 1));

        Assert.Equal(string.Empty, paint.Sung);
        Assert.Equal("ABCDE", paint.Current);
        Assert.Equal(string.Empty, paint.Remaining);
    }

    [Fact]
    public void Split_ACursorPastTheEndOfTheSyllables_IsClamped()
    {
        var paint = LyricTimeline.Split(Document[0], new LyricCursor(0, 99));

        Assert.Equal("晴", paint.Sung);
        Assert.Equal("天", paint.Current);
    }

    // ---- the three-line window ------------------------------------------------------------------

    [Fact]
    public void WindowAt_BeforeTheFirstLine_ShowsItAsTheUpcomingLineRatherThanNothing()
    {
        // An instrumental intro reads as "something is coming" instead of as an empty box.
        var window = LyricTimeline.WindowAt(new LyricsDocument(Document), -1);

        Assert.Equal(string.Empty, window.Previous);
        Assert.Equal("晴天", window.Next);
        Assert.Equal(LyricLinePaint.Empty, window.Current);
        Assert.False(window.HasCurrent);
        Assert.Equal(-1, window.CurrentIndex);
    }

    [Fact]
    public void WindowAt_MidSong_ShowsTheLineEitherSide()
    {
        var window = LyricTimeline.WindowAt(new LyricsDocument(Document), 2_900);

        Assert.Equal("晴天", window.Previous);
        Assert.Equal("词：周", window.Current.Sung + window.Current.Current + window.Current.Remaining);
        Assert.Equal("（间奏，无逐字）", window.Next);
        Assert.Equal(1, window.CurrentIndex);
    }

    [Fact]
    public void WindowAt_OnTheFirstLine_HasNothingAboveIt()
    {
        Assert.Equal(string.Empty, LyricTimeline.WindowAt(new LyricsDocument(Document), 100).Previous);
    }

    [Fact]
    public void WindowAt_OnTheLastLine_HasNothingBelowIt()
    {
        Assert.Equal(string.Empty, LyricTimeline.WindowAt(new LyricsDocument(Document), 5_000).Next);
    }

    [Fact]
    public void WindowAt_PastTheEnd_KeepsTheLastLineActiveInsteadOfBlanking()
    {
        var window = LyricTimeline.WindowAt(new LyricsDocument(Document), 999_999);

        Assert.Equal(2, window.CurrentIndex);
        Assert.Equal("（间奏，无逐字）", window.Current.Current);
    }

    [Fact]
    public void WindowAt_ASingleLineDocument_HasNoNeighboursOnEitherSide()
    {
        var document = new LyricsDocument(new[] { new LyricLine(0, 1_000, "唯一一行") });

        var window = LyricTimeline.WindowAt(document, 500);

        Assert.Equal(string.Empty, window.Previous);
        Assert.Equal(string.Empty, window.Next);
        Assert.Equal("唯一一行", window.Current.Current);
    }

    [Fact]
    public void WindowAt_AnEmptyDocument_ShowsNothingAtAll()
    {
        Assert.Equal(LyricsWindow.Empty, LyricTimeline.WindowAt(LyricsDocument.Empty, 1_000));
    }

    [Fact]
    public void WindowAt_DoesNotAllocateTextPerFrame_CursorAloneDecidesWhetherToRepaint()
    {
        // The render loop compares cursors rather than windows: a cursor carry-only record struct
        // compares without touching any string, which is what keeps a per-frame check free.
        var document = new LyricsDocument(Document);
        var first = LyricTimeline.Locate(document.Lines, 100);
        var second = LyricTimeline.Locate(document.Lines, 150);

        Assert.Equal(first, second);
        Assert.NotEqual(first, LyricTimeline.Locate(document.Lines, 200));
    }

    // ---- the document ---------------------------------------------------------------------------

    [Fact]
    public void LyricsDocument_ReportsSyllablesWhenAnyLineHasThem()
    {
        Assert.True(new LyricsDocument(Document).HasSyllables);
    }

    [Fact]
    public void LyricsDocument_AnLrcOnlySource_ReportsNoSyllablesAndIsStillValid()
    {
        // Not an error: LRCLIB has no other kind, and the card degrades to whole-line highlighting.
        var document = new LyricsDocument(new[] { new LyricLine(0, 1_000, "只有整行时间戳") });

        Assert.False(document.HasSyllables);
        Assert.False(document.IsEmpty);
    }

    [Fact]
    public void LyricsDocument_Empty_IsEmptyAndSilent()
    {
        Assert.True(LyricsDocument.Empty.IsEmpty);
        Assert.False(LyricsDocument.Empty.HasSyllables);
        Assert.Equal(-1, LyricTimeline.LineIndexAt(LyricsDocument.Empty.Lines, 1_000));
    }
}
