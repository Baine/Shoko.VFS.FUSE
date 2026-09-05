using Shoko.Abstractions.Metadata;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Resolvers;
using Shoko.VFS.FUSE.Resolvers.Relay;

namespace Shoko.VFS.FUSE.Tests.Resolvers;

public sealed class RelaySeriesGrouperTests
{
    [Fact]
    public void ParsesManualCsvCommentsInvalidIdsAndDuplicates()
    {
        var groups = RelaySeriesGrouper.ParseManualOverrides(
            ["", " # comment", "1, 2, nope, 2, 0, -4", "3", "bad,0"]
        );

        Assert.Equal(new[] { 1, 2 }, groups.Single());
    }

    [Fact]
    public void AutomaticTmdbGroupsOrderPrimaryAndDeterministicallyMergeMappings()
    {
        var late = Input(20, 200, new PartialDateOnly(2022, 1, 1), 900, Mapping(20, 1));
        var early = Input(30, 300, new PartialDateOnly(2020, 1, 1), 900, Mapping(30, 2), Mapping(20, 1));

        var groups = RelaySeriesGrouper.Group([late, early], mergeTmdbSeries: true);

        var group = Assert.Single(groups);
        Assert.Equal(30, group.PrimarySeriesId);
        Assert.Equal(new[] { 30, 20 }, group.SeriesIds);
        Assert.Equal(new[] { 30, 20 }, group.Data.Mappings.Select(mapping => mapping.FileId));
    }

    [Fact]
    public void ManualGroupsWinOverAutomaticTmdbGroupsAndUseFirstIdAsPrimary()
    {
        var first = Input(10, 100, new PartialDateOnly(2020, 1, 1), 900, Mapping(10, 1));
        var second = Input(20, 200, new PartialDateOnly(2021, 1, 1), 900, Mapping(20, 2));
        var third = Input(30, 300, new PartialDateOnly(2019, 1, 1), 900, Mapping(30, 3));

        var groups = RelaySeriesGrouper.Group([first, second, third], mergeTmdbSeries: true, manualOverrides: [[200, 100]]);

        Assert.Equal(new[] { 20, 10 }, groups[0].SeriesIds);
        Assert.Equal(20, groups[0].PrimarySeriesId);
        Assert.Equal(new[] { 30 }, groups[1].SeriesIds);
    }

    [Fact]
    public void GroupClosureUsesManualGroupsBeforeAutomaticGroups()
    {
        var metadata = new[]
        {
            new RelaySeriesGroupingMetadata(30, 300, new PartialDateOnly(2019, 1, 1), 900),
            new RelaySeriesGroupingMetadata(10, 100, new PartialDateOnly(2020, 1, 1), 900),
            new RelaySeriesGroupingMetadata(20, 200, new PartialDateOnly(2021, 1, 1), 900),
        };

        var closure = RelaySeriesGrouper.GetGroupClosure(metadata, [20], mergeTmdbSeries: true, manualOverrides: [[100, 200]]);

        Assert.Equal(new[] { 10, 20 }, closure.OrderBy(id => id));
    }

    [Fact]
    public void GroupClosureIncludesAllAutomaticMembers()
    {
        var metadata = new[]
        {
            new RelaySeriesGroupingMetadata(20, 200, new PartialDateOnly(2022, 1, 1), 900),
            new RelaySeriesGroupingMetadata(30, 300, new PartialDateOnly(2020, 1, 1), 900),
            new RelaySeriesGroupingMetadata(10, 100, new PartialDateOnly(2021, 1, 1), 900),
            new RelaySeriesGroupingMetadata(40, 400, new PartialDateOnly(2020, 1, 1), 901),
        };

        var closure = RelaySeriesGrouper.GetGroupClosure(metadata, [20], mergeTmdbSeries: true);

        Assert.Equal(new[] { 10, 20, 30 }, closure.OrderBy(id => id));
    }

    private static RelaySeriesGroupingInput Input(int id, int anidbId, PartialDateOnly airDate, int tmdbId, params EpisodeData[] mappings) =>
        new(new SeriesData(id, $"Series {id}", false, mappings), anidbId, airDate, tmdbId);

    private static EpisodeData Mapping(int fileId, int episode) =>
        new(fileId, fileId, 1, episode, null, null, 1, false, true, null, $"/source/{fileId}.mkv", 0, ".mkv");
}
