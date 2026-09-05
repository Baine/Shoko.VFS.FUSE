using System.Text.RegularExpressions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.VFS.FUSE.Models;

namespace Shoko.VFS.FUSE.Resolvers.Relay;

internal sealed record RelayRawSeries(
    int SeriesId,
    AnimeType Type,
    string? DisplayTitle,
    IReadOnlyList<RelayRawEpisode> Episodes)
{
    internal bool UseTmdbNumbering { get; init; }
    internal bool IsTmdbMovie { get; init; }
    internal string? PreferredTmdbOrderingId { get; init; }
    internal string? RawPreferredTmdbOrderingId { get; init; }
    internal int AnidbAnimeId { get; init; }
    internal PartialDateOnly? AirDate { get; init; }
    internal int? TmdbSeriesId { get; init; }
};

internal sealed record RelayRawEpisode(
    int EpisodeId,
    EpisodeType Type,
    int EpisodeNumber,
    int? SeasonNumber,
    bool IsHidden,
    string? Title,
    IReadOnlyList<RelayRawVideo> Videos)
{
    internal IReadOnlyList<RelayRawTmdbEpisode> TmdbEpisodes { get; init; } = [];
};

internal sealed record RelayRawVideo(
    int VideoId,
    bool IsVariation,
    string? SortPath,
    string? SourcePath,
    long Size,
    string Extension,
    IReadOnlyList<RelayRawCrossReference> CrossReferences)
{
    internal bool IsExcluded { get; init; }
};

internal sealed record RelayRawCrossReference(int? EpisodeId, int? SeriesId);

internal sealed record RelayRawTmdbEpisode(
    int? SeasonNumber,
    int EpisodeNumber,
    string OrderingId,
    IReadOnlyList<RelayRawTmdbOrdering> AllOrderings,
    string? Title);

internal sealed record RelayRawTmdbOrdering(string OrderingId, int SeasonNumber, int EpisodeNumber);

internal static class RelayMappingProjector
{
    private static readonly Regex s_plexSplitTagRegex = new(@"(?ix)(?:^|[\s._-])(cd|disc|disk|dvd|part|pt)[\s._-]*([1-8])(?!\d)", RegexOptions.Compiled);

    internal static SeriesData Project(RelayRawSeries series)
    {
        var visibleEpisodes = series.Episodes.Where(episode => !episode.IsHidden && episode.Videos.Count > 0).ToList();
        var coordinates = visibleEpisodes.ToDictionary(episode => episode.EpisodeId, episode => GetCoordinates(episode, series));
        var episodeVideoCounts = visibleEpisodes.ToDictionary(episode => episode.EpisodeId, episode => episode.Videos.Count);
        var videoEpisodes = new Dictionary<int, List<EpisodeCoordinate>>();

        foreach (var episode in visibleEpisodes)
        {
            var coordinate = coordinates[episode.EpisodeId];
            foreach (var video in episode.Videos)
                videoEpisodes.GetOrAdd(video.VideoId).Add(new EpisodeCoordinate(episode, coordinate));
        }

        var videos = visibleEpisodes
            .SelectMany(episode => episode.Videos)
            .DistinctBy(video => video.VideoId)
            .OrderBy(video => FileNameSortKey(video.SortPath), StringComparer.Ordinal)
            .ThenBy(video => video.VideoId)
            .ToList();
        bool hasSeasonOne = visibleEpisodes.Any(episode => coordinates[episode.EpisodeId].Season == 1);
        bool hasSpecials = visibleEpisodes.Any(episode => coordinates[episode.EpisodeId].Season == 0);
        var mappings = new List<EpisodeData>(videos.Count);

        foreach (var video in videos)
        {
            if (!videoEpisodes.TryGetValue(video.VideoId, out var associatedEpisodes))
                continue;

            var sortedEpisodes = associatedEpisodes
                .OrderBy(entry => entry.Coordinates.Season)
                .ThenBy(entry => entry.Coordinates.Episode)
                .ToList();

            int distinctSeriesCount = video.CrossReferences
                .Where(reference => reference.EpisodeId.HasValue && reference.SeriesId.HasValue)
                .Select(reference => reference.SeriesId!.Value)
                .Distinct()
                .Count();
            if (sortedEpisodes.Count > 1
                && sortedEpisodes.Select(entry => entry.Episode.Type).Distinct().Count() > 1
                && distinctSeriesCount > 1)
            {
                int? firstXrefId = video.CrossReferences
                    .FirstOrDefault(reference => reference.EpisodeId is int episodeId && sortedEpisodes.Any(entry => entry.Episode.EpisodeId == episodeId))
                    ?.EpisodeId;
                if (firstXrefId is int episodeId)
                {
                    var primaryType = sortedEpisodes.First(entry => entry.Episode.EpisodeId == episodeId).Episode.Type;
                    sortedEpisodes = sortedEpisodes.Where(entry => entry.Episode.Type == primaryType).ToList();
                }
            }

            var deduped = DeduplicateByCoordinates(sortedEpisodes, video.CrossReferences);
            if (deduped.Count == 0)
                continue;

            var primary = deduped[0].Episode;
            var mappedCoordinates = deduped.Count > 1
                && (episodeVideoCounts.GetValueOrDefault(primary.EpisodeId) > 1 || deduped.Select(entry => entry.Episode.Type).Distinct().Count() == 1)
                ? GetCoordinatesForFile(deduped, series)
                : deduped[0].Coordinates;

            if (mappedCoordinates.Season == -4)
            {
                mappedCoordinates = !hasSeasonOne
                    ? mappedCoordinates with { Season = 1 }
                    : !hasSpecials ? mappedCoordinates with { Season = 0 } : mappedCoordinates;
            }

            int partCount = episodeVideoCounts.GetValueOrDefault(primary.EpisodeId);
            var firstEpisodeVideos = primary.Videos
                .DistinctBy(candidate => candidate.VideoId)
                .OrderBy(candidate => FileNameSortKey(candidate.SortPath), StringComparer.Ordinal)
                .ThenBy(candidate => candidate.VideoId)
                .ToList();
            int partIndex = firstEpisodeVideos.FindIndex(candidate => candidate.VideoId == video.VideoId);
            bool allowParts = partCount > 1
                && partIndex >= 0
                && deduped.Select(entry => entry.Episode.Type).Distinct().Count() <= 1
                && IsSplitVideo(video);

            mappings.Add(
                new EpisodeData(
                    video.VideoId,
                    primary.EpisodeId,
                    mappedCoordinates.Season,
                    mappedCoordinates.Episode,
                    mappedCoordinates.EndEpisode,
                    allowParts ? partIndex + 1 : null,
                    allowParts ? partCount : 1,
                    video.IsVariation,
                    primary.Type == EpisodeType.Episode,
                    primary.Title,
                    video.SourcePath,
                    video.Size,
                    video.Extension
                )
            );
        }

        bool isMovie = series.UseTmdbNumbering ? series.IsTmdbMovie : series.Type == AnimeType.Movie;
        return new SeriesData(series.SeriesId, series.DisplayTitle, isMovie, mappings);
    }

    private static List<EpisodeCoordinate> DeduplicateByCoordinates(List<EpisodeCoordinate> entries, IReadOnlyList<RelayRawCrossReference> crossReferences)
    {
        var xrefPositions = new Dictionary<int, int>();
        for (int i = 0; i < crossReferences.Count; i++)
        {
            if (crossReferences[i].EpisodeId is int episodeId && !xrefPositions.ContainsKey(episodeId))
                xrefPositions.Add(episodeId, i);
        }

        var deduped = new List<EpisodeCoordinate>();
        var coordinateIndexes = new Dictionary<(int Season, int Episode), int>();
        foreach (var entry in entries)
        {
            var key = (entry.Coordinates.Season, entry.Coordinates.Episode);
            if (!coordinateIndexes.TryGetValue(key, out int existingIndex))
            {
                coordinateIndexes.Add(key, deduped.Count);
                deduped.Add(entry);
                continue;
            }

            int existingPosition = xrefPositions.GetValueOrDefault(deduped[existingIndex].Episode.EpisodeId, -1);
            int newPosition = xrefPositions.GetValueOrDefault(entry.Episode.EpisodeId, -1);
            if (newPosition >= 0 && (existingPosition == -1 || newPosition < existingPosition))
                deduped[existingIndex] = entry;
        }

        if (deduped.Count > 1 && xrefPositions.Count > 0)
        {
            int bestIndex = 0;
            int bestPosition = xrefPositions.GetValueOrDefault(deduped[0].Episode.EpisodeId, int.MaxValue);
            for (int i = 1; i < deduped.Count; i++)
            {
                int position = xrefPositions.GetValueOrDefault(deduped[i].Episode.EpisodeId, int.MaxValue);
                if (position < bestPosition)
                {
                    bestIndex = i;
                    bestPosition = position;
                }
            }

            if (bestIndex > 0)
            {
                var best = deduped[bestIndex];
                deduped.RemoveAt(bestIndex);
                deduped.Insert(0, best);
            }
        }

        return deduped;
    }

    private static RelayCoordinates GetCoordinates(RelayRawEpisode episode, RelayRawSeries series)
    {
        if (series.UseTmdbNumbering && TryGetTmdbCoordinates(episode.TmdbEpisodes, series.PreferredTmdbOrderingId, out var tmdbCoordinates))
            return tmdbCoordinates;

        return GetShokoCoordinates(episode);
    }

    private static RelayCoordinates GetShokoCoordinates(RelayRawEpisode episode)
    {
        int season = episode.SeasonNumber
            ?? episode.Type switch
            {
                EpisodeType.Episode => 1,
                EpisodeType.Special => 0,
                EpisodeType.Credits => -1,
                EpisodeType.Trailer => -2,
                EpisodeType.Parody => -3,
                EpisodeType.Other => -4,
                _ => -9,
            };

        return episode.Type switch
        {
            EpisodeType.Other => new RelayCoordinates(-4, episode.EpisodeNumber, null),
            EpisodeType.Credits => new RelayCoordinates(-1, episode.EpisodeNumber, null),
            EpisodeType.Trailer => new RelayCoordinates(-2, episode.EpisodeNumber, null),
            EpisodeType.Parody => new RelayCoordinates(-3, episode.EpisodeNumber, null),
            _ => new RelayCoordinates(season, episode.EpisodeNumber, null),
        };
    }

    private static RelayCoordinates GetCoordinatesForFile(IReadOnlyList<EpisodeCoordinate> episodes, RelayRawSeries series)
    {
        if (series.UseTmdbNumbering && episodes.Select(entry => entry.Episode.Type).Distinct().Count() == 1)
        {
            var tmdbEpisodes = episodes.SelectMany(entry => entry.Episode.TmdbEpisodes).ToList();
            if (TryGetTmdbCoordinates(tmdbEpisodes, series.RawPreferredTmdbOrderingId, out var tmdbCoordinates))
                return tmdbCoordinates;
        }

        var first = episodes[0].Coordinates;
        var last = episodes[^1].Coordinates;
        return first with { EndEpisode = first.Season == last.Season ? last.Episode : null };
    }

    private static bool TryGetTmdbCoordinates(
        IReadOnlyList<RelayRawTmdbEpisode> episodes,
        string? preferredOrderingId,
        out RelayCoordinates coordinates)
    {
        var ordered = SelectPreferredTmdbOrdering(episodes, preferredOrderingId);
        if (ordered.Count == 0)
        {
            coordinates = default!;
            return false;
        }

        var first = GetOrderingCoordinates(ordered[0], preferredOrderingId);
        if (!first.Season.HasValue)
        {
            coordinates = default!;
            return false;
        }

        var last = GetOrderingCoordinates(ordered[^1], preferredOrderingId);
        coordinates = new RelayCoordinates(first.Season.Value, first.Episode, ordered.Count > 1 && last.Season == first.Season ? last.Episode : null);
        return true;
    }

    private static List<RelayRawTmdbEpisode> SelectPreferredTmdbOrdering(
        IEnumerable<RelayRawTmdbEpisode> episodes,
        string? preferredOrderingId)
    {
        var list = episodes.ToList();
        if (list.Count == 0 || string.IsNullOrWhiteSpace(preferredOrderingId))
            return [.. list.OrderBy(episode => episode.SeasonNumber ?? 0).ThenBy(episode => episode.EpisodeNumber)];

        return [..
            list.Select(episode =>
                    (
                        Episode: episode,
                        Priority: string.Equals(episode.OrderingId, preferredOrderingId, StringComparison.OrdinalIgnoreCase) ? 0
                            : episode.AllOrderings.Any(ordering => string.Equals(ordering.OrderingId, preferredOrderingId, StringComparison.OrdinalIgnoreCase)) ? 1
                            : 2
                    )
                )
                .OrderBy(item => item.Priority)
                .ThenBy(item => item.Episode.SeasonNumber ?? 0)
                .ThenBy(item => item.Episode.EpisodeNumber)
                .Select(item => item.Episode)
        ];
    }

    private static (int? Season, int Episode) GetOrderingCoordinates(RelayRawTmdbEpisode episode, string? preferredOrderingId)
    {
        if (!string.IsNullOrWhiteSpace(preferredOrderingId))
        {
            var ordering = episode.AllOrderings.FirstOrDefault(candidate => string.Equals(candidate.OrderingId, preferredOrderingId, StringComparison.OrdinalIgnoreCase));
            if (ordering is not null)
                return (ordering.SeasonNumber, ordering.EpisodeNumber);
        }

        return (episode.SeasonNumber, episode.EpisodeNumber);
    }

    private static bool IsSplitVideo(RelayRawVideo video) => s_plexSplitTagRegex.IsMatch(
        Path.GetFileNameWithoutExtension(video.SortPath ?? string.Empty).Replace('[', ' ').Replace(']', ' '));

    private static string FileNameSortKey(string? path) => Path.GetFileName(path ?? string.Empty).Replace('\\', '/');

    private sealed record EpisodeCoordinate(RelayRawEpisode Episode, RelayCoordinates Coordinates);

    private sealed record RelayCoordinates(int Season, int Episode, int? EndEpisode);

    private static List<TValue> GetOrAdd<TKey, TValue>(this IDictionary<TKey, List<TValue>> dictionary, TKey key)
        where TKey : notnull
    {
        if (!dictionary.TryGetValue(key, out var values))
        {
            values = [];
            dictionary.Add(key, values);
        }

        return values;
    }
}
