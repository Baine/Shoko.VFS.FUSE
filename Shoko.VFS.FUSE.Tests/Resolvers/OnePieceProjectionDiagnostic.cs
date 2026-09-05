using System.Text.Json;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.VFS.FUSE.Resolvers;
using Shoko.VFS.FUSE.Resolvers.Relay;

namespace Shoko.VFS.FUSE.Tests.Resolvers;

/// <summary>
/// One-shot diagnostic for the One Piece projection bug: feed real Shoko API data for
/// AniDB series 69 into <see cref="RelayMappingProjector"/> and report where episodes
/// get dropped.
/// </summary>
public sealed class OnePieceProjectionDiagnostic
{
    private sealed class EpListDto
    {
        public int Total { get; set; }
        public List<EpDto>? List { get; set; }
    }

    private sealed class EpDto
    {
        public IdsDto? IDs { get; set; }
        public bool? IsHidden { get; set; }
        public string? Name { get; set; }
        public AnidbDto? AniDB { get; set; }
        public List<FileDto>? Files { get; set; }
        public TmdbBlockDto? TMDB { get; set; }
    }

    private sealed class IdsDto
    {
        public int ID { get; set; }
        public int AniDB { get; set; }
    }

    private sealed class AnidbDto
    {
        public string? Type { get; set; }
        public int? EpisodeNumber { get; set; }
    }

    private sealed class FileDto
    {
        public int ID { get; set; }
        public bool? IsVariation { get; set; }
    }

    private sealed class TmdbBlockDto
    {
        public List<TmdbEpDto>? Episodes { get; set; }
    }

    private sealed class TmdbEpDto
    {
        public int ID { get; set; }
        public int? SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public string? AlternateOrderingID { get; set; }
        public string? Title { get; set; }
    }

    [Fact]
    public void RunOnePieceDiagnostic()
    {
        var path = Environment.GetEnvironmentVariable("ONE_PIECE_JSON") ?? "/tmp/opencode/onepiece/op_eps.json";
        Assert.True(File.Exists(path), $"One Piece JSON not found at {path}");

        var json = File.ReadAllText(path);
        var wrapper = JsonSerializer.Deserialize<EpListDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to parse");
        var episodes = wrapper.List ?? throw new InvalidOperationException("Missing List property");

        Console.WriteLine($"Total episodes parsed: {episodes.Count}");

        var rawEpisodes = new List<RelayRawEpisode>(episodes.Count);
        int noFilesCount = 0, noTmdbCount = 0, parsedCount = 0;

        foreach (var ep in episodes)
        {
            if (ep.IDs is null || ep.IDs.ID == 0) continue;
            if (ep.Files is null || ep.Files.Count == 0)
            {
                noFilesCount++;
                continue;
            }
            var tmdbEps = (ep.TMDB?.Episodes ?? [])
                .Select(t => new RelayRawTmdbEpisode(t.SeasonNumber, t.EpisodeNumber, t.AlternateOrderingID ?? "", [], t.Title))
                .ToList();
            if (tmdbEps.Count == 0)
            {
                noTmdbCount++;
                continue;
            }

            int anidbEpNum = ep.AniDB?.EpisodeNumber ?? 0;
            var videos = ep.Files
                .Select(f => new RelayRawVideo(f.ID, f.IsVariation ?? false, $"/mnt/user/array/Anime/Shows/GerDub/One Piece/s{anidbEpNum}_v{f.ID}.mkv", null, 1_000_000, ".mkv", []))
                .ToList();

            var episodeType = Enum.TryParse<EpisodeType>(ep.AniDB?.Type, ignoreCase: true, out var t) ? t : EpisodeType.Episode;
            rawEpisodes.Add(new RelayRawEpisode(ep.IDs.ID, episodeType, anidbEpNum, null, ep.IsHidden ?? false, ep.Name, videos) with
            {
                TmdbEpisodes = tmdbEps,
            });
            parsedCount++;
        }

        Console.WriteLine($"Skipped (no files): {noFilesCount}");
        Console.WriteLine($"Skipped (no TMDB linkage): {noTmdbCount}");
        Console.WriteLine($"Parsed RelayRawEpisodes: {parsedCount}");

        var raw = new RelayRawSeries(69, AnimeType.TV, "One Piece", rawEpisodes)
        {
            UseTmdbNumbering = true,
            PreferredTmdbOrderingId = null,           // matches One Piece (no series-level TMDB show link)
            RawPreferredTmdbOrderingId = null,
            AnidbAnimeId = 69,
            TmdbSeriesId = null,                       // matches One Piece
        };

        var projected = RelayMappingProjector.Project(raw);
        Console.WriteLine($"Projected SeriesData: SeriesId={projected.SeriesId} DisplayTitle={projected.DisplayTitle} IsMovie={projected.IsMovie}");
        Console.WriteLine($"Projected mappings count: {projected.Mappings.Count}");

        var bySeason = projected.Mappings
            .GroupBy(m => m.Season)
            .OrderBy(g => g.Key)
            .Select(g => $"  Season {g.Key}: {g.Count()} mappings")
            .ToList();
        Console.WriteLine("Mappings by season:");
        foreach (var line in bySeason) Console.WriteLine(line);

        // Count what *should* have been produced (raw TMDB season distribution from input).
        var expectedBySeason = rawEpisodes
            .SelectMany(e => e.TmdbEpisodes)
            .Where(t => t.SeasonNumber.HasValue)
            .GroupBy(t => t.SeasonNumber!.Value)
            .OrderBy(g => g.Key)
            .Select(g => $"  TMDB Season {g.Key}: {g.Count()} episode xrefs")
            .ToList();
        Console.WriteLine("Expected TMDB season distribution (from input):");
        foreach (var line in expectedBySeason) Console.WriteLine(line);
    }
}
