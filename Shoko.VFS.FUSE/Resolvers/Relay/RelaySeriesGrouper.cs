using System.Globalization;
using Shoko.Abstractions.Metadata;
using Shoko.VFS.FUSE.Models;

namespace Shoko.VFS.FUSE.Resolvers.Relay;

/// <summary>Metadata needed to apply Relay's series merge policy.</summary>
public sealed record RelaySeriesGroupingInput(
    SeriesData Data,
    int AnidbAnimeId = 0,
    PartialDateOnly? AirDate = null,
    int? TmdbSeriesId = null)
{
    public int SeriesId => Data.SeriesId;
}

internal sealed record RelaySeriesGroupingMetadata(
    int SeriesId,
    int AnidbAnimeId = 0,
    PartialDateOnly? AirDate = null,
    int? TmdbSeriesId = null);

/// <summary>A deterministic Relay series group with its merged mapping projection.</summary>
public sealed record RelaySeriesGroup(
    int PrimarySeriesId,
    IReadOnlyList<int> SeriesIds,
    SeriesData Data);

/// <summary>Pure manual and automatic Relay series grouping.</summary>
public static class RelaySeriesGrouper
{
    public const string OverrideFileName = "anidb_vfs_overrides.csv";

    public static string GetOverridePath(string dataPath) => Path.Combine(dataPath, "Shoko.VFS.FUSE", OverrideFileName);

    /// <summary>Parses Relay's one-group-per-line override CSV grammar without file I/O.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> ParseManualOverrides(IEnumerable<string>? lines)
    {
        var groups = new List<IReadOnlyList<int>>();
        foreach (var raw in lines ?? [])
        {
            var line = raw?.Trim() ?? "";
            if (line.Length == 0 || line[0] == '#')
                continue;

            var ids = line
                .Split(',')
                .Select(value => int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .ToArray();
            if (ids.Length >= 2)
                groups.Add(ids);
        }

        return groups;
    }

    public static IReadOnlyList<IReadOnlyList<int>> ParseManualOverrides(string? csv) =>
        ParseManualOverrides(csv?.Split(["\r\n", "\n", "\r"], StringSplitOptions.None));

    public static IReadOnlyList<IReadOnlyList<int>> ParseOverrides(IEnumerable<string>? lines) => ParseManualOverrides(lines);

    public static IReadOnlyList<RelaySeriesGroup> Group(
        IEnumerable<RelaySeriesGroupingInput> inputs,
        bool mergeTmdbSeries = false,
        IEnumerable<IReadOnlyList<int>>? manualOverrides = null)
    {
        var entries = (inputs ?? [])
            .Where(input => input.Data is not null)
            .DistinctBy(input => input.SeriesId)
            .ToList();
        var byId = entries.ToDictionary(input => input.SeriesId);
        return GroupIds(
                entries.Select(ToMetadata),
                mergeTmdbSeries,
                manualOverrides
            )
            .Select(group =>
            {
                var primary = byId[group.SeriesIds[0]];
                return new RelaySeriesGroup(
                    primary.SeriesId,
                    group.SeriesIds,
                    MergeData(group.SeriesIds.Select(id => byId[id].Data))
                );
            })
            .ToArray();
    }

    internal static IReadOnlySet<int> GetGroupClosure(
        IEnumerable<RelaySeriesGroupingMetadata> inputs,
        IEnumerable<int> seedSeriesIds,
        bool mergeTmdbSeries = false,
        IEnumerable<IReadOnlyList<int>>? manualOverrides = null)
    {
        var seeds = (seedSeriesIds ?? []).ToHashSet();
        return GroupIds(inputs, mergeTmdbSeries, manualOverrides)
            .Where(group => group.SeriesIds.Any(seeds.Contains))
            .SelectMany(group => group.SeriesIds)
            .ToHashSet();
    }

    public static IReadOnlyList<SeriesData> Merge(
        IEnumerable<RelaySeriesGroupingInput> inputs,
        bool mergeTmdbSeries = false,
        IEnumerable<IReadOnlyList<int>>? manualOverrides = null) =>
        Group(inputs, mergeTmdbSeries, manualOverrides).Select(group => group.Data).ToArray();

    private static SeriesData MergeData(IEnumerable<SeriesData> series)
    {
        var list = series.ToList();
        var primary = list[0];
        var mappings = list
            .SelectMany(item => item.Mappings ?? [])
            .DistinctBy(mapping => (mapping.FileId, mapping.Season, mapping.Episode, mapping.EndEpisode, mapping.PartIndex, mapping.IsVariation))
            .ToArray();
        return primary with { Mappings = mappings };
    }

    private static IReadOnlyList<RelaySeriesIdGroup> GroupIds(
        IEnumerable<RelaySeriesGroupingMetadata> inputs,
        bool mergeTmdbSeries,
        IEnumerable<IReadOnlyList<int>>? manualOverrides)
    {
        // ponytail: first-wins on duplicate SeriesId so one bad input can't kill a whole
        // snapshot build; callers dedupe upstream (ShokoRestClient.GetAllSeriesAsync).
        var entries = (inputs ?? []).ToList();
        var byId = entries
            .GroupBy(input => input.SeriesId)
            .ToDictionary(group => group.Key, group => group.First());
        var byAnidbId = entries
            .Where(input => input.AnidbAnimeId > 0)
            .GroupBy(input => input.AnidbAnimeId)
            .ToDictionary(group => group.Key, group => group.First());
        var assigned = new HashSet<int>();
        var groups = new List<RelaySeriesIdGroup>();

        void AddGroup(IReadOnlyList<int> ids, int firstInputIndex)
        {
            var present = ids.Where(byId.ContainsKey).Distinct().ToArray();
            if (present.Length == 0)
                return;

            foreach (var id in present)
                assigned.Add(id);
            groups.Add(new RelaySeriesIdGroup(firstInputIndex, present));
        }

        foreach (var overrideGroup in manualOverrides ?? [])
        {
            var present = overrideGroup
                .Where(byAnidbId.ContainsKey)
                .Select(id => byAnidbId[id].SeriesId)
                .Distinct()
                .ToArray();
            if (present.Length == 0 || assigned.Contains(present[0]))
                continue;
            AddGroup(present, entries.FindIndex(input => input.SeriesId == present[0]));
        }

        if (mergeTmdbSeries)
        {
            foreach (var automatic in entries
                         .Where(input => input.TmdbSeriesId.HasValue)
                         .GroupBy(input => input.TmdbSeriesId!.Value)
                         .Where(group => group.Count() > 1))
            {
                if (automatic.Any(input => assigned.Contains(input.SeriesId)))
                    continue;

                var ids = automatic
                    .OrderBy(input => input.AirDate ?? PartialDateOnly.MaxValue)
                    .ThenBy(input => input.AnidbAnimeId)
                    .ThenBy(input => input.SeriesId)
                    .Select(input => input.SeriesId)
                    .ToArray();
                AddGroup(ids, entries.FindIndex(input => input.SeriesId == ids[0]));
            }
        }

        foreach (var entry in entries)
            if (!assigned.Contains(entry.SeriesId))
                AddGroup([entry.SeriesId], entries.IndexOf(entry));

        return groups
            .OrderBy(group => group.FirstInputIndex)
            .ThenBy(group => group.SeriesIds[0])
            .ToArray();
    }

    private static RelaySeriesGroupingMetadata ToMetadata(RelaySeriesGroupingInput input) =>
        new(input.SeriesId, input.AnidbAnimeId, input.AirDate, input.TmdbSeriesId);

    private sealed record RelaySeriesIdGroup(int FirstInputIndex, IReadOnlyList<int> SeriesIds);
}
