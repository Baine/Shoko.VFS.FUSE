using Shoko.VFS.FUSE.Resolvers.Fin;

namespace Shoko.VFS.FUSE.Tests.Resolvers.Fin;

public sealed class FinSeasonOrderingTests
{
    private static FinOrderingEpisode Ep(
        string seasonId, int id, int aniDbNum,
        FinEpisodeType type = FinEpisodeType.Episode,
        DateTimeOffset? aired = null,
        bool hidden = false,
        bool mainEntry = true,
        FinExtraType? extraType = null,
        string? title = null)
        => new(seasonId, id, type, aniDbNum, aired, hidden, mainEntry, extraType, title);

    // --- Basic bucketing ---

    [Fact]
    public void Build_NormalEpisodes_GoIntoEpisodeBucket()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [
                Ep("S1", 1, 1, aired: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)),
                Ep("S1", 2, 2, aired: new DateTimeOffset(2020, 1, 8, 0, 0, 0, TimeSpan.Zero)),
            ]);

        var results = FinSeasonOrdering.Build([input]);
        var r = Assert.Single(results);

        Assert.Equal(2, r.Episodes.Count);
        Assert.Empty(r.Specials);
        Assert.Empty(r.Extras);
        Assert.Empty(r.AlternateEpisodes);
    }

    [Fact]
    public void Build_Specials_GoIntoSpecialsBucket()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [
                Ep("S1", 1, 1),
                Ep("S1", 10, 1, FinEpisodeType.Special),
            ]);

        var r = Assert.Single(FinSeasonOrdering.Build([input]));

        Assert.Single(r.Episodes);
        Assert.Single(r.Specials);
        Assert.Equal(10, r.Specials[0].EpisodeId);
    }

    [Fact]
    public void Build_Extras_GoIntoExtrasBucket()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [
                Ep("S1", 1, 1),
                Ep("S1", 20, 1, FinEpisodeType.Credits, extraType: FinExtraType.ThemeVideo),
            ]);

        var r = Assert.Single(FinSeasonOrdering.Build([input]));

        Assert.Single(r.Episodes);
        Assert.Single(r.Extras);
    }

    [Fact]
    public void Build_OtherWithNoExtraType_GoesToAlternateEpisodes()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [
                Ep("S1", 1, 1),
                Ep("S1", 2, 1, FinEpisodeType.Other),
            ]);

        var r = Assert.Single(FinSeasonOrdering.Build([input]));

        Assert.Single(r.Episodes);
        Assert.Single(r.AlternateEpisodes);
    }

    [Fact]
    public void Build_HiddenEpisodes_AreSkipped()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [
                Ep("S1", 1, 1),
                Ep("S1", 2, 2, hidden: true),
                Ep("S1", 3, 3),
            ]);

        var r = Assert.Single(FinSeasonOrdering.Build([input]));

        Assert.Equal(2, r.Episodes.Count);
        Assert.All(r.Episodes, e => Assert.NotEqual(2, e.EpisodeId));
    }

    // --- Airdate ordering ---

    [Fact]
    public void Build_OrderByAirdate_SortsByAiredAt()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], true, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [
                Ep("S1", 10, 2, aired: new DateTimeOffset(2020, 1, 8, 0, 0, 0, TimeSpan.Zero)),
                Ep("S1", 1, 1, aired: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            ]);

        var r = Assert.Single(FinSeasonOrdering.Build([input]));

        Assert.Equal(1, r.Episodes[0].EpisodeId);
        Assert.Equal(10, r.Episodes[1].EpisodeId);
    }

    [Fact]
    public void Build_EpisodesWithoutAiredAt_SortLast()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], true, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [
                Ep("S1", 10, 2, aired: new DateTimeOffset(2020, 1, 8, 0, 0, 0, TimeSpan.Zero)),
                Ep("S1", 1, 1),
            ]);

        var r = Assert.Single(FinSeasonOrdering.Build([input]));

        // Episodes without AiredAt sort last when OrderByAirdate=true
        Assert.Equal(10, r.Episodes[0].EpisodeId);
        Assert.Equal(1, r.Episodes[1].EpisodeId);
    }

    // --- Episode-number ordering (OrderByAirdate=false) ---

    [Fact]
    public void Build_NotOrderByAirdate_SortsByAniDbEpisodeNumber()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [
                Ep("S1", 10, 5),
                Ep("S1", 1, 1),
                Ep("S1", 5, 3),
            ]);

        var r = Assert.Single(FinSeasonOrdering.Build([input]));

        Assert.Equal(1, r.Episodes[0].EpisodeId);
        Assert.Equal(5, r.Episodes[1].EpisodeId);
        Assert.Equal(10, r.Episodes[2].EpisodeId);
    }

    // --- Episode numbering ---

    [Fact]
    public void Build_EpisodeNumbers_AreOneBasedSequential()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [
                Ep("S1", 100, 5),
                Ep("S1", 200, 1),
                Ep("S1", 300, 3),
            ]);

        var r = Assert.Single(FinSeasonOrdering.Build([input]));

        Assert.Equal(1, r.Episodes[0].EpisodeNumber);
        Assert.Equal(2, r.Episodes[1].EpisodeNumber);
        Assert.Equal(3, r.Episodes[2].EpisodeNumber);
    }

    [Fact]
    public void Build_Specials_EpisodeNumbers_AreOneBased()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [
                Ep("S1", 1, 1),
                Ep("S1", 10, 1, FinEpisodeType.Special),
                Ep("S1", 11, 2, FinEpisodeType.Special),
            ]);

        var r = Assert.Single(FinSeasonOrdering.Build([input]));

        Assert.Equal(1, r.Specials[0].EpisodeNumber);
        Assert.Equal(2, r.Specials[1].EpisodeNumber);
    }

    // --- Episode conversions ---

    [Fact]
    public void Build_EpisodesAsSpecials_ConvertsNormalToSpecials()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.EpisodesAsSpecials, SpecialOrderingType.Excluded,
            [
                Ep("S1", 1, 1),
                Ep("S1", 2, 2),
            ]);

        var r = Assert.Single(FinSeasonOrdering.Build([input]));

        Assert.Empty(r.Episodes);
        Assert.Equal(2, r.Specials.Count);
        Assert.All(r.Specials, s => Assert.True(s.IsSpecial));
    }

    [Fact]
    public void Build_SpecialsAsEpisodes_ConvertsSpecialsToNormal()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.SpecialsAsEpisodes, SpecialOrderingType.Excluded,
            [
                Ep("S1", 1, 1),
                Ep("S1", 10, 1, FinEpisodeType.Special),
            ]);

        var r = Assert.Single(FinSeasonOrdering.Build([input]));

        Assert.Equal(2, r.Episodes.Count);
        Assert.Empty(r.Specials);
    }

    [Fact]
    public void Build_SpecialsAsExtraFeaturettes_MovesSpecialsAndAltsToExtras()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.SpecialsAsExtraFeaturettes, SpecialOrderingType.Excluded,
            [
                Ep("S1", 1, 1),
                Ep("S1", 10, 1, FinEpisodeType.Special),
                Ep("S1", 20, 1, FinEpisodeType.Other),
            ]);

        var r = Assert.Single(FinSeasonOrdering.Build([input]));

        Assert.Single(r.Episodes);
        Assert.Empty(r.Specials);
        Assert.Empty(r.AlternateEpisodes);
        Assert.Equal(2, r.Extras.Count);
    }

    // --- Movie→Web fallback ---

    [Fact]
    public void Build_EmptyEpisodes_AltEpisodesPromoted()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [
                Ep("S1", 1, 1, FinEpisodeType.Other),
                Ep("S1", 2, 2, FinEpisodeType.Other),
            ],
            FinSeriesType.Movie);

        var r = Assert.Single(FinSeasonOrdering.Build([input]));

        Assert.Equal(2, r.Episodes.Count);
        Assert.Empty(r.AlternateEpisodes);
        Assert.True(r.WasPromotedToAlt);
        Assert.Equal("Web", r.PromotedSeriesType);
    }

    // --- Multi-season ---

    [Fact]
    public void Build_MultipleSeasons_EachGetsOwnBuckets()
    {
        var s1 = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [Ep("S1", 1, 1), Ep("S1", 2, 2)]);

        var s2 = new FinSeasonOrderingInput(
            "S2", [], false, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [Ep("S2", 10, 1), Ep("S2", 11, 2), Ep("S2", 12, 3)]);

        var results = FinSeasonOrdering.Build([s1, s2]);

        Assert.Equal(2, results.Count);
        Assert.Equal(2, results[0].Episodes.Count);
        Assert.Equal(3, results[1].Episodes.Count);
    }

    // --- Base-season-number mapping (resolved input, not inferred) ---

    [Fact]
    public void Build_NormalEpisodes_UseResolvedBase()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [Ep("S1", 1, 1), Ep("S1", 2, 2)]);

        var results = FinSeasonOrdering.Build([input], new Dictionary<string, int> { ["S1"] = 3 });

        var r = Assert.Single(results);
        Assert.Equal(3, r.SeasonNumber);
        Assert.All(r.Episodes, e => Assert.Equal(3, e.SeasonNumber));
    }

    [Fact]
    public void Build_AlternateEpisodes_UseBasePlusOne()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [
                Ep("S1", 1, 1),
                Ep("S1", 2, 1, FinEpisodeType.Other),
            ]);

        var results = FinSeasonOrdering.Build([input], new Dictionary<string, int> { ["S1"] = 5 });

        var r = Assert.Single(results);
        Assert.Equal(5, r.Episodes[0].SeasonNumber);
        Assert.Single(r.AlternateEpisodes);
        Assert.Equal(6, r.AlternateEpisodes[0].SeasonNumber);
    }

    [Fact]
    public void Build_MissingBaseMapping_YieldsZero()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [Ep("S1", 1, 1)]);

        var results = FinSeasonOrdering.Build([input], new Dictionary<string, int>());

        var r = Assert.Single(results);
        Assert.Equal(0, r.SeasonNumber);
        Assert.Equal(0, r.Episodes[0].SeasonNumber);
    }

    [Fact]
    public void Build_DistinctBases_KeepDistinctNumbers()
    {
        var s1 = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [Ep("S1", 1, 1)]);

        var s2 = new FinSeasonOrderingInput(
            "S2", [], false, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [Ep("S2", 10, 1)]);

        var bases = new Dictionary<string, int> { ["S1"] = 2, ["S2"] = 7 };
        var results = FinSeasonOrdering.Build([s1, s2], bases);

        Assert.Equal(2, results[0].SeasonNumber);
        Assert.Equal(7, results[1].SeasonNumber);
        Assert.Equal(2, results[0].Episodes[0].SeasonNumber);
        Assert.Equal(7, results[1].Episodes[0].SeasonNumber);
    }

    [Fact]
    public void Build_NullBaseMapping_AllSeasonNumbersAreZero()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [Ep("S1", 1, 1)]);

        var results = FinSeasonOrdering.Build([input]);

        var r = Assert.Single(results);
        Assert.Equal(0, r.SeasonNumber);
        Assert.Equal(0, r.Episodes[0].SeasonNumber);
    }

    // --- Placed episode flags ---

    [Fact]
    public void Build_PlacedEpisodes_HaveCorrectFlags()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded,
            [
                Ep("S1", 1, 1),
                Ep("S1", 2, 2, FinEpisodeType.Special),
                Ep("S1", 3, 1, FinEpisodeType.Other),
                Ep("S1", 4, 1, FinEpisodeType.Credits, extraType: FinExtraType.ThemeVideo),
            ]);

        var r = Assert.Single(FinSeasonOrdering.Build([input]));

        Assert.Single(r.Episodes);
        Assert.False(r.Episodes[0].IsSpecial);
        Assert.False(r.Episodes[0].IsExtra);
        Assert.False(r.Episodes[0].IsAlternate);

        Assert.Single(r.Specials);
        Assert.True(r.Specials[0].IsSpecial);

        Assert.Single(r.AlternateEpisodes);
        Assert.True(r.AlternateEpisodes[0].IsAlternate);

        Assert.Single(r.Extras);
        Assert.True(r.Extras[0].IsExtra);
    }

    // --- Empty input ---

    [Fact]
    public void Build_EmptyEpisodesList_ReturnsEmptyBuckets()
    {
        var input = new FinSeasonOrderingInput(
            "S1", [], false, SeriesEpisodeConversion.None, SpecialOrderingType.Excluded, []);

        var r = Assert.Single(FinSeasonOrdering.Build([input]));

        Assert.Empty(r.Episodes);
        Assert.Empty(r.Specials);
        Assert.Empty(r.Extras);
        Assert.Empty(r.AlternateEpisodes);
    }
}
