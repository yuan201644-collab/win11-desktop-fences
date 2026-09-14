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

    // ---- resolving the line for the renderer ----------------------------------------------------

    [Fact]
    public void Split_ALineWithNoSyllables_ReportsTheWholeLineAsCurrent()
    {
        // The "highlight the whole line" degradation, expressed through the same two counters as the
        // karaoke case, so the card needs only one code path.
        var paint = LyricTimeline.Split(Document[2], new LyricCursor(2, 0));

        Assert.Equal("（间奏，无逐字）", paint.Text);
        Assert.Equal(8, paint.ElementCount);
        Assert.Equal(0, paint.SungElements);
        Assert.Equal(8, paint.CurrentElements);
    }

    [Fact]
    public void Split_BeforeAnySyllableOfALine_LeavesEverythingAhead()
    {
        var paint = LyricTimeline.Split(Document[0], new LyricCursor(0, 0));

        Assert.Equal("晴天", paint.Text);
        Assert.Equal(2, paint.ElementCount);
        Assert.Equal(0, paint.SungElements);
        Assert.Equal(0, paint.CurrentElements);
        Assert.False(paint.IsFullySung);
    }

    [Fact]
    public void Split_OnTheFirstSyllable_HighlightsItAndLeavesTheRestAhead()
    {
        var paint = LyricTimeline.Split(Document[0], new LyricCursor(0, 1));

        Assert.Equal("晴天", paint.Text);
        Assert.Equal(0, paint.SungElements);
        Assert.Equal(1, paint.CurrentElements);
    }

    [Fact]
    public void Split_OnTheLastSyllable_LeavesNothingUnaccountedFor()
    {
        var paint = LyricTimeline.Split(Document[1], new LyricCursor(1, 3));

        Assert.Equal("词：周", paint.Text);
        Assert.Equal(3, paint.ElementCount);
        Assert.Equal(2, paint.SungElements);
        Assert.Equal(1, paint.CurrentElements);
        Assert.True(paint.IsFullySung);
    }

    [Fact]
    public void Split_TheCountsAlwaysPartitionTheLineWithoutOverlappingOrRunningOffTheEnd()
    {
        // The card gives every character one inline and colours it from these two counters, so a count
        // that overran would colour nothing — or index nothing at all.
        for (var line = 0; line < Document.Length; line++)
        {
            var source = Document[line];
            for (var count = 0; count <= source.Syllables.Count; count++)
            {
                var paint = LyricTimeline.Split(source, new LyricCursor(line, count));

                Assert.Equal(source.Text, paint.Text);
                Assert.InRange(paint.SungElements, 0, paint.ElementCount);
                Assert.InRange(paint.CurrentElements, 0, paint.ElementCount - paint.SungElements);
            }
        }
    }

    [Fact]
    public void Split_PreservesWhitespaceSyllablesSoLatinLyricsKeepTheirWordBreaks()
    {
        // QRC publishes the space between two English words as its own syllable. Dropping or
        // normalising it would run the words together — and it has to be counted like any other
        // character, or every word after it would light up one place too early.
        var line = Built(0, ("Hel", 0, 200), ("lo", 200, 400), (" ", 400, 450), ("world", 450, 900));

        var paint = LyricTimeline.Split(line, new LyricCursor(0, 3));

        Assert.Equal("Hello world", paint.Text);
        Assert.Equal(11, paint.ElementCount);
        Assert.Equal(5, paint.SungElements);
        Assert.Equal(1, paint.CurrentElements);
    }

    [Fact]
    public void Split_ASingleSyllableLine_IsCurrentFromTheStart()
    {
        var line = Built(0, ("啊", 0, 500));

        var paint = LyricTimeline.Split(line, new LyricCursor(0, 1));

        Assert.Equal("啊", paint.Text);
        Assert.Equal(1, paint.ElementCount);
        Assert.Equal(0, paint.SungElements);
        Assert.Equal(1, paint.CurrentElements);
    }

    [Fact]
    public void Split_CountsAnEmojiAsOneCharacterSoTheRendererCannotCutItInHalf()
    {
        // A surrogate pair is two UTF-16 code units but one character to a reader. The card builds one
        // inline per character and colours them by index, so counting code units here would leave every
        // line containing an emoji permanently off by one — and would hand the text engine a lone half.
        // Written as an escape so the test says what it means without depending on the file's encoding.
        const string note = "\U0001F3B5";
        var line = Built(0, ("晴", 0, 100), (note, 100, 300), ("天", 300, 500));

        var paint = LyricTimeline.Split(line, new LyricCursor(0, 2));

        Assert.Equal(4, paint.Text.Length);          // UTF-16 code units, for contrast
        Assert.Equal(3, paint.ElementCount);         // what a reader sees
        Assert.Equal(1, paint.SungElements);
        Assert.Equal(1, paint.CurrentElements);
        Assert.False(paint.IsFullySung);
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

        Assert.Equal("AB", paint.Text);
        Assert.Equal(0, paint.SungElements);
        Assert.Equal(2, paint.CurrentElements);
    }

    [Fact]
    public void Split_ACursorPastTheEndOfTheSyllables_IsClamped()
    {
        var paint = LyricTimeline.Split(Document[0], new LyricCursor(0, 99));

        Assert.Equal(1, paint.SungElements);
        Assert.Equal(1, paint.CurrentElements);
        Assert.True(paint.IsFullySung);
    }

    [Fact]
    public void Split_ReportsTheLineTextVerbatim_SoTheCardCanTellWhenToRebuildItsInlines()
    {
        // The card keeps one inline per character and only rebuilds when this string changes. A
        // normalised or trimmed copy would make two different lines compare equal and leave stale
        // glyphs on screen.
        const string awkward = "  Spaced  出  ";

        var paint = LyricTimeline.Split(new LyricLine(0, 100, awkward), new LyricCursor(0, 0));

        Assert.Equal(awkward, paint.Text);
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
        Assert.Equal("词：周", window.Current.Text);
        Assert.Equal(2, window.Current.SungElements);
        Assert.Equal(1, window.Current.CurrentElements);
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
        Assert.Equal("（间奏，无逐字）", window.Current.Text);
    }

    [Fact]
    public void WindowAt_ASingleLineDocument_HasNoNeighboursOnEitherSide()
    {
        var document = new LyricsDocument(new[] { new LyricLine(0, 1_000, "唯一一行") });

        var window = LyricTimeline.WindowAt(document, 500);

        Assert.Equal(string.Empty, window.Previous);
        Assert.Equal(string.Empty, window.Next);
        Assert.Equal("唯一一行", window.Current.Text);
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
