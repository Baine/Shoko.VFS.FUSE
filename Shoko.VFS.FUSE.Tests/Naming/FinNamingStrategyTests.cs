using Shoko.VFS.FUSE.Naming;

namespace Shoko.VFS.FUSE.Tests.Naming;

public class FinNamingStrategyTests
{
    private readonly FinNamingStrategy _sut = new();

    [Fact]
    public void RootsAreFlat_AndExtrasAreNotTvSeasons()
    {
        Assert.Equal("fin", _sut.Consumer);
        Assert.Null(_sut.TvRootFolderName);
        Assert.Null(_sut.MovieRootFolderName);
        Assert.False(_sut.IsTvExtraSeason(-1));
        Assert.False(_sut.IsTvExtraSeason(0));
        Assert.False(_sut.IsTvExtraSeason(1));
    }

    [Fact]
    public void FormatSeriesFolder_WithTitle()
    {
        Assert.Equal("Steins;Gate [ShokoSeries=42]", _sut.FormatSeriesFolder(42, "Steins;Gate"));
    }

    [Fact]
    public void FormatSeriesFolder_NullTitle_UsesFallback()
    {
        Assert.Equal("Series 42 [ShokoSeries=42]", _sut.FormatSeriesFolder(42, null));
    }

    [Fact]
    public void FormatSeriesFolder_LongTitle_TruncatesAt64Characters()
    {
        Assert.Equal(new string('A', 64) + " [ShokoSeries=1]", _sut.FormatSeriesFolder(1, new string('A', 100)));
    }

    [Theory]
    [InlineData(0, "Season 00")]
    [InlineData(1, "Season 01")]
    [InlineData(12, "Season 12")]
    public void FormatSeasonFolder_PadsWithZeros(int season, string expected)
    {
        Assert.Equal(expected, _sut.FormatSeasonFolder(season));
    }

    [Fact]
    public void FormatEpisodeFileName_IncludesMetadataAndSeriesTitle()
    {
        Assert.Equal(
            "S01E05 [ShokoFile=999].mkv",
            _sut.FormatEpisodeFileName(new EpisodeFileContext(1, 5, null, 3, 999, ".mkv", null, false, null, null, null, false))
        );
        Assert.Equal(
            "Series S02E03-E05 [ShokoFile=888].mkv",
            _sut.FormatEpisodeFileName(new EpisodeFileContext(2, 3, 5, 2, 888, ".mkv", "Series", false, null, null, null, false))
        );
    }

    [Fact]
    public void FormatMovieFileNameAndFolder_IncludeShokoMetadata()
    {
        Assert.Equal(
            "Movie [ShokoEpisode=777] [ShokoFile=888].mkv",
            _sut.FormatMovieFileName(new MovieFileContext(777, 888, ".mkv", false, null, null, null, false))
        );
        Assert.Equal("Movie 777", _sut.FormatMovieFolder(777));
    }

    [Fact]
    public void FormatFileNames_PreserveFinMultipartDuplicateAndVariationConventions()
    {
        Assert.Equal(
            "S01E02-pt1.mkv",
            _sut.FormatEpisodeFileName(new EpisodeFileContext(1, 2, null, 2, 7, ".mkv", null, true, 1, 2, null, false))
        );
        Assert.Equal(
            "S01E02-dup2 [ShokoFile=7].mkv",
            _sut.FormatEpisodeFileName(new EpisodeFileContext(1, 2, null, 2, 7, ".mkv", null, false, null, 1, 2, false))
        );
        Assert.Equal(
            "S01E02 [ShokoFile=7][variation].mkv",
            _sut.FormatEpisodeFileName(new EpisodeFileContext(1, 2, null, 2, 7, ".mkv", null, false, null, null, null, true))
        );
    }

    [Fact]
    public void ExtrasMethods_AreNotSupported()
    {
        Assert.Throws<NotSupportedException>(() => _sut.FormatExtrasFolder(-4));
        Assert.Throws<NotSupportedException>(
            () => _sut.FormatExtrasFileName(new ExtrasFileContext(-4, 1, 2, "Title", ".mkv", null, null, null, false))
        );
    }
}
