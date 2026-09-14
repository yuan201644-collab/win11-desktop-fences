using DesktopMediaController.Core;

namespace DesktopMediaController.Tests;

/// <summary>
/// Splitting matters more than it looks: measured against a real chart, one track in nine is a
/// collaboration, and the separator left in the query is the difference between a comfortable match
/// and one sitting on the acceptance threshold.
/// </summary>
public sealed class ArtistNamesTests
{
    [Fact]
    public void Split_ABlankArtist_NamesNobody()
    {
        Assert.Empty(ArtistNames.Split(null));
        Assert.Empty(ArtistNames.Split(string.Empty));
        Assert.Empty(ArtistNames.Split("   "));
    }

    [Fact]
    public void Split_ASingleArtist_ComesBackAsOneName()
    {
        Assert.Equal(new[] { "周杰伦" }, ArtistNames.Split("周杰伦"));
    }

    [Theory]
    [InlineData("周杰伦/费玉清")]
    [InlineData("周杰伦／费玉清")]
    [InlineData("周杰伦、费玉清")]
    [InlineData("周杰伦;费玉清")]
    [InlineData("周杰伦；费玉清")]
    [InlineData("周杰伦,费玉清")]
    [InlineData("周杰伦，费玉清")]
    [InlineData("周杰伦|费玉清")]
    [InlineData("周杰伦×费玉清")]
    public void Split_TheDividersChinesePlatformsActuallyUse_AllBreakTheName(string packed)
    {
        Assert.Equal(new[] { "周杰伦", "费玉清" }, ArtistNames.Split(packed));
    }

    [Theory]
    [InlineData("A feat. B")]
    [InlineData("A Feat. B")]
    [InlineData("A FEAT. B")]
    [InlineData("A ft. B")]
    [InlineData("A FT. B")]
    [InlineData("A vs. B")]
    public void Split_AFeaturedArtistMarker_BreaksTheNameWhateverItsCasing(string packed)
    {
        Assert.Equal(new[] { "A", "B" }, ArtistNames.Split(packed));
    }

    [Fact]
    public void Split_ASpacedAmpersand_BreaksTheNameToMatchTheLibrarysOwnReaders()
    {
        // Deliberate, and copied from the library we consume: its LRCLIB reader splits the incoming
        // artist string on " & " too. Splitting our side differently from theirs is what produces a
        // mismatch, not the split itself.
        Assert.Equal(new[] { "A", "B" }, ArtistNames.Split("A & B"));
    }

    [Fact]
    public void Split_AnUnspacedAmpersand_IsPartOfTheNameNotADivider()
    {
        // A bare ampersand is how a great many single acts are named; requiring the spaces is what
        // keeps them in one piece.
        Assert.Equal(new[] { "Garfunkel&Simon" }, ArtistNames.Split("Garfunkel&Simon"));
    }

    [Fact]
    public void Split_FragmentsAreTrimmedAndBlanksDropped()
    {
        Assert.Equal(new[] { "A", "B" }, ArtistNames.Split("  A  /  B  "));
        Assert.Equal(new[] { "A" }, ArtistNames.Split("A//"));
    }

    [Fact]
    public void Split_AWordThatMerelyStartsLikeAMarker_DoesNotBreakTheName()
    {
        // "ft" and "feat" only count with a space on both sides, so a name that happens to begin with
        // them survives intact.
        Assert.Equal(new[] { "A featx B" }, ArtistNames.Split("A featx B"));
        Assert.Equal(new[] { "A ft" }, ArtistNames.Split("A ft"));
    }

    [Fact]
    public void Split_ThreeArtists_AllOfThemComeBack()
    {
        Assert.Equal(new[] { "A", "B", "C" }, ArtistNames.Split("A,B,C"));
    }

    [Fact]
    public void Join_UsesThePlatformsOwnDivider()
    {
        Assert.Equal("周杰伦 / 费玉清", ArtistNames.Join(new[] { "周杰伦", "费玉清" }));
    }
}
