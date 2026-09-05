using Shoko.VFS.FUSE.Naming;

namespace Shoko.VFS.FUSE.Tests.Naming;

public class RelayNamingStrategyTests
{
    private readonly RelayNamingStrategy _sut = new();

    [Fact]
    public void Roots_AreOwnedByMountPlanning()
    {
        Assert.Equal("relay", _sut.Consumer);
        Assert.Null(_sut.TvRootFolderName);
        Assert.Null(_sut.MovieRootFolderName);
    }

    [Fact]
    public void Roots_AreNeverInnerResolverChildren()
    {
        Assert.DoesNotContain("!ShokoRelayVFS", new[] { _sut.TvRootFolderName, _sut.MovieRootFolderName });
        Assert.DoesNotContain("!ShokoRelayMovieVFS", new[] { _sut.TvRootFolderName, _sut.MovieRootFolderName });
    }

    [Theory]
    [InlineData(-9, "Other", true)]
    [InlineData(-4, "Featurettes", true)]
    [InlineData(-3, "Scenes", true)]
    [InlineData(-2, "Trailers", true)]
    [InlineData(-1, "Shorts", true)]
    [InlineData(0, "Specials", false)]
    [InlineData(1, "Season 1", false)]
    [InlineData(12, "Season 12", false)]
    public void SeasonFolders_MapRelaySeasons(int season, string expected, bool isExtra)
    {
        Assert.Equal(expected, _sut.FormatSeasonFolder(season));
        Assert.Equal(isExtra, _sut.IsTvExtraSeason(season));
    }

    [Fact]
    public void SeriesFolder_IsTheSeriesId()
    {
        Assert.Equal("12345", _sut.FormatSeriesFolder(12345, "Some Title"));
        Assert.Equal("relay", _sut.Consumer);
    }

    [Fact]
    public void EpisodeFileName_UsesDynamicPaddingAndRanges()
    {
        Assert.Equal(
            "S02E003 [888].mkv",
            _sut.FormatEpisodeFileName(
                new EpisodeFileContext(2, 3, null, 3, 888, ".mkv", null, false, null, null, null, false)
            )
        );
        Assert.Equal(
            "S02E003-E005 [888].mkv",
            _sut.FormatEpisodeFileName(
                new EpisodeFileContext(2, 3, 5, 3, 888, ".mkv", null, false, null, null, null, false)
            )
        );
    }

    [Fact]
    public void EpisodeFileName_PlacesDuplicatePartAndVariationTagsExactly()
    {
        Assert.Equal(
            "S01E02-pt2.mkv",
            _sut.FormatEpisodeFileName(new EpisodeFileContext(1, 2, null, 2, 7, ".mkv", null, true, 2, 3, null, false))
        );
        Assert.Equal(
            "S01E02-dup3 [7].mkv",
            _sut.FormatEpisodeFileName(new EpisodeFileContext(1, 2, null, 2, 7, ".mkv", null, false, null, 1, 3, false))
        );
        Assert.Equal(
            "S01E02 [7][variation].mkv",
            _sut.FormatEpisodeFileName(new EpisodeFileContext(1, 2, null, 2, 7, ".mkv", null, false, null, null, null, true))
        );
    }

    [Fact]
    public void MovieFileNameAndFolder_UseRelayIds()
    {
        Assert.Equal(
            "Movie [888].mkv",
            _sut.FormatMovieFileName(new MovieFileContext(777, 888, ".mkv", false, null, null, null, false))
        );
        Assert.Equal("777", _sut.FormatMovieFolder(777));
    }

    [Theory]
    [InlineData(-1, "C")]
    [InlineData(-2, "T")]
    [InlineData(-3, "P")]
    [InlineData(-4, "O")]
    [InlineData(-9, "U")]
    [InlineData(-8, "O")]
    public void ExtrasFileName_UsesSubtypePrefixAndFallback(int season, string prefix)
    {
        var result = _sut.FormatExtrasFileName(new ExtrasFileContext(season, 3, 3, "Title", ".mkv", null, null, null, false));

        Assert.Equal($"{prefix}003 ❯ Title.mkv", result);
    }

    [Fact]
    public void ExtrasFileName_PlacesPartsDuplicatesAndVariationBeforeTitle()
    {
        Assert.Equal(
            "T02-pt1 ❯ Title.mkv",
            _sut.FormatExtrasFileName(new ExtrasFileContext(-2, 2, 2, "Title", ".mkv", 1, 2, 9, false))
        );
        Assert.Equal(
            "O02-dup4 ❯ Title.mkv",
            _sut.FormatExtrasFileName(new ExtrasFileContext(-4, 2, 2, "Title", ".mkv", null, 1, 4, false))
        );
        Assert.Equal(
            "U02 ❯ Title[variation].mkv",
            _sut.FormatExtrasFileName(new ExtrasFileContext(-9, 2, 2, "Title", ".mkv", null, null, null, true))
        );
    }

    [Fact]
    public void ExtrasFileName_CleansAndSanitizesRelayTitle()
    {
        const string expectedTitle = "O\u200Bpening “½” ⁄ ½꞉ test？";

        Assert.Equal(
            $"C01 ❯ {expectedTitle}.mkv",
            _sut.FormatExtrasFileName(new ExtrasFileContext(-1, 1, 2, "Opening \"1/2\" / 1/2: test?", ".mkv", null, null, null, false))
        );
    }
}
