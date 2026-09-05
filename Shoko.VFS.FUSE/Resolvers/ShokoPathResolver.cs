using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Naming;

namespace Shoko.VFS.FUSE.Resolvers;

/// <summary>Resolves Shoko data into a naming-strategy-specific virtual filesystem.</summary>
public sealed class ShokoPathResolver : IVirtualPathResolver
{
    private readonly IShokoPathDataSource? _dataSource;
    private readonly IVirtualTreeDataSource? _treeDataSource;
    private readonly IPathNamingStrategy? _naming;
    private readonly PathResolverOptions _options;
    private readonly ILogger _logger;
    private readonly AutoResetEvent _refreshSignal = new(false);
    private readonly Thread _refreshWorker;
    private readonly object _publishGate = new();
    private ResolverSnapshot? _snapshot;
    private int _refreshQueued;
    private int _refreshRunning;
    private int _dirty;
    private int _stopped;
    private long _retryAfterUtcTicks;

    public ShokoPathResolver(IShokoPathDataSource dataSource, IPathNamingStrategy naming, PathResolverOptions? options = null)
        : this(dataSource, naming, options, null)
    {
    }

    public ShokoPathResolver(IVirtualTreeDataSource dataSource, PathResolverOptions? options = null)
        : this(dataSource, options, null)
    {
    }

    public ShokoPathResolver(
        IShokoPathDataSource dataSource,
        IPathNamingStrategy naming,
        PathResolverOptions? options,
        ILogger? logger)
    {
        _dataSource = dataSource;
        _naming = naming;
        _options = options ?? new PathResolverOptions();
        _logger = logger ?? NullLogger<ShokoPathResolver>.Instance;
        _refreshWorker = new Thread(RefreshLoop)
        {
            IsBackground = true,
            Name = "Shoko VFS resolver refresh",
        };
        _refreshWorker.Start();
        RequestRefresh(forced: false);
    }

    public ShokoPathResolver(
        IVirtualTreeDataSource dataSource,
        PathResolverOptions? options,
        ILogger? logger)
    {
        _treeDataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _options = options ?? new PathResolverOptions();
        _logger = logger ?? NullLogger<ShokoPathResolver>.Instance;
        _refreshWorker = new Thread(RefreshLoop)
        {
            IsBackground = true,
            Name = "Shoko VFS resolver refresh",
        };
        _refreshWorker.Start();
        RequestRefresh(forced: false);
    }

    public IReadOnlyList<VirtualEntry> ReadDirectory(string mountRelativePath)
    {
        var segments = NormalizePath(mountRelativePath);
        var snapshot = CaptureSnapshotAndRequestRefresh();
        return snapshot is null
            ? Array.Empty<VirtualEntry>()
            : ResolveDirectory(snapshot, segments) ?? Array.Empty<VirtualEntry>();
    }

    public VirtualEntry? Lookup(string mountRelativePath)
    {
        var segments = NormalizePath(mountRelativePath);
        var snapshot = CaptureSnapshotAndRequestRefresh();
        if (segments.Length == 0)
            return new VirtualEntry
            {
                Name = "root",
                NodeType = VirtualNodeType.Directory,
                Mode = VirtualEntry.DefaultMode(VirtualNodeType.Directory),
            };

        if (snapshot is null)
            return null;

        var parent = ResolveDirectory(snapshot, segments[..^1]);
        return parent?.FirstOrDefault(entry => string.Equals(entry.Name, segments[^1], StringComparison.Ordinal));
    }

    public string? GetSourcePath(string mountRelativePath) => Lookup(mountRelativePath)?.SourcePath;

    public void Invalidate(string mountRelativePath, bool includeChildren = false)
    {
        RequestRefresh(forced: true);
    }

    public void Rebuild() => RequestRefresh(forced: true);

    internal void Stop()
    {
        lock (_publishGate)
        {
            if (Volatile.Read(ref _stopped) != 0)
                return;
            Volatile.Write(ref _stopped, 1);
            Interlocked.Exchange(ref _refreshRunning, 2);
        }

        // Do not join the worker: a datasource fetch may be non-interruptible.
        _refreshSignal.Set();
    }

    internal bool HasSnapshot => Volatile.Read(ref _snapshot) is not null;

    private IReadOnlyList<VirtualEntry>? ResolveDirectory(ResolverSnapshot snapshot, string[] segments)
    {
        return snapshot.Tree.ReadDirectory(segments);
    }

    private SnapshotBuildResult BuildSnapshot()
    {
        var stopwatch = Stopwatch.StartNew();
        if (_treeDataSource is not null)
        {
            // Copy the datasource-owned collection before publishing anything.
            var entries = (_treeDataSource.GetEntries() ?? Array.Empty<VirtualTreeEntryData>()).ToArray();
            var treeCompletedAt = DateTimeOffset.UtcNow;
            var tree = VirtualTreeSnapshot.Build(entries, legacyFirstWins: false, treeCompletedAt);
            stopwatch.Stop();
            return new SnapshotBuildResult(
                new ResolverSnapshot(tree, treeCompletedAt),
                stopwatch.Elapsed,
                0,
                entries.Length,
                entries.Count(entry => entry.NodeType == VirtualNodeType.File),
                0,
                0,
                0,
                tree.ReadDirectory(Array.Empty<string>()).Count,
                0,
                0
            );
        }

        var series = _dataSource!.GetAllSeries() ?? Array.Empty<SeriesData>();
        var models = new List<SeriesModel>(series.Count);
        int mappingCount = 0;
        int mappingsWithSourcePathCount = 0;
        foreach (var item in series)
        {
            var mappings = item.Mappings ?? Array.Empty<EpisodeData>();
            mappingCount += mappings.Count;
            mappingsWithSourcePathCount += mappings.Count(mapping => !string.IsNullOrWhiteSpace(mapping.SourcePath));
            models.Add(BuildModel(item));
        }

        var rootChildren = GetRootChildren(models).ToArray();
        var tvSeriesChildren = GetTvSeriesChildren(models).ToArray();
        var movieFolderChildren = GetMovieFolderChildren(models).ToArray();
        var completedAt = DateTimeOffset.UtcNow;
        var legacyEntries = BuildLegacyEntries(rootChildren, tvSeriesChildren, movieFolderChildren, completedAt);
        var treeSnapshot = VirtualTreeSnapshot.Build(legacyEntries, legacyFirstWins: true, completedAt);
        stopwatch.Stop();

        return new SnapshotBuildResult(
            new ResolverSnapshot(
                treeSnapshot,
                completedAt
            ),
            stopwatch.Elapsed,
            series.Count,
            mappingCount,
            mappingsWithSourcePathCount,
            models.Count,
            models.Count(model => model.Tv is not null),
            models.Sum(model => model.Movies.Count),
            rootChildren.Length,
            tvSeriesChildren.Length,
            movieFolderChildren.Length
        );
    }

    private List<RootChild> GetRootChildren(IReadOnlyList<SeriesModel> models)
    {
        var children = new List<RootChild>();

        if (_naming!.TvRootFolderName is string tvRoot)
        {
            if (models.Any(model => model.Tv is not null))
                children.Add(new RootChild(tvRoot, RootChildKind.TvRoot));
        }
        else
        {
            foreach (var model in models.Where(model => model.Tv is not null))
                children.Add(new RootChild(model.SeriesFolderName!, RootChildKind.TvSeries, model));
        }

        if (_naming!.MovieRootFolderName is string movieRoot)
        {
            if (models.Any(model => model.Movies.Count > 0))
                children.Add(new RootChild(movieRoot, RootChildKind.MovieRoot));
        }
        else
        {
            foreach (var model in models)
                foreach (var movie in model.Movies)
                    children.Add(new RootChild(movie.FolderName, RootChildKind.MovieFolder, model, movie));
        }

        return DistinctRootChildren(children);
    }

    private List<RootChild> GetTvSeriesChildren(IReadOnlyList<SeriesModel> models)
    {
        var children = models
            .Where(model => model.Tv is not null)
            .Select(model => new RootChild(model.SeriesFolderName!, RootChildKind.TvSeries, model));
        return DistinctRootChildren(children);
    }

    private List<RootChild> GetMovieFolderChildren(IReadOnlyList<SeriesModel> models)
    {
        var children = models
            .SelectMany(model => model.Movies.Select(movie => new RootChild(movie.FolderName, RootChildKind.MovieFolder, model, movie)));
        return DistinctRootChildren(children);
    }

    private static List<VirtualTreeEntryData> BuildLegacyEntries(
        IReadOnlyList<RootChild> rootChildren,
        IReadOnlyList<RootChild> tvSeriesChildren,
        IReadOnlyList<RootChild> movieFolderChildren,
        DateTimeOffset lastModified)
    {
        var entries = new List<VirtualTreeEntryData>();
        foreach (var child in rootChildren)
        {
            AddDirectory(entries, child.Name, lastModified);
            switch (child.Kind)
            {
                case RootChildKind.TvRoot:
                    foreach (var series in tvSeriesChildren)
                        AddTvSeries(entries, child.Name + "/" + series.Name, series.Series!, lastModified);
                    break;
                case RootChildKind.MovieRoot:
                    foreach (var movie in movieFolderChildren)
                        AddMovie(entries, child.Name + "/" + movie.Name, movie.Movie!, lastModified);
                    break;
                case RootChildKind.TvSeries:
                    AddTvSeries(entries, child.Name, child.Series!, lastModified);
                    break;
                case RootChildKind.MovieFolder:
                    AddMovie(entries, child.Name, child.Movie!, lastModified);
                    break;
            }
        }

        return entries;
    }

    private static void AddTvSeries(
        List<VirtualTreeEntryData> entries,
        string path,
        SeriesModel series,
        DateTimeOffset lastModified)
    {
        AddDirectory(entries, path, lastModified);
        // Series-folder-root extras (AnimeThemes Theme.mp3, etc.) — siblings of the
        // season directories, so Plex/Jellyfin clients see them at the series level.
        foreach (var extra in series.Tv!.ExtraFiles)
            AddFile(entries, path + "/" + extra.Name, extra, lastModified);
        foreach (var season in series.Tv!.Seasons)
        {
            string seasonPath = path + "/" + season.Name;
            AddDirectory(entries, seasonPath, lastModified);
            foreach (var file in season.Files)
                AddFile(entries, seasonPath + "/" + file.Name, file, lastModified);
        }
    }

    private static void AddMovie(
        List<VirtualTreeEntryData> entries,
        string path,
        MovieModel movie,
        DateTimeOffset lastModified)
    {
        AddDirectory(entries, path, lastModified);
        // Movie-folder-root extras (AnimeThemes Theme.mp3 mirrored per-episode by ShokoRelay).
        foreach (var extra in movie.ExtraFiles)
            AddFile(entries, path + "/" + extra.Name, extra, lastModified);
        foreach (var file in movie.Files)
            AddFile(entries, path + "/" + file.Name, file, lastModified);
        foreach (var extra in movie.Extras.OrderBy(extra => extra.Name, StringComparer.Ordinal))
        {
            string extraPath = path + "/" + extra.Name;
            AddDirectory(entries, extraPath, lastModified);
            foreach (var file in extra.Files)
                AddFile(entries, extraPath + "/" + file.Name, file, lastModified);
        }
    }

    private static void AddDirectory(List<VirtualTreeEntryData> entries, string path, DateTimeOffset lastModified) =>
        entries.Add(new VirtualTreeEntryData(
            path,
            VirtualNodeType.Directory,
            Mode: VirtualEntry.DefaultMode(VirtualNodeType.Directory),
            LastModified: lastModified
        ));

    private static void AddFile(List<VirtualTreeEntryData> entries, string path, FileModel file, DateTimeOffset lastModified) =>
        entries.Add(new VirtualTreeEntryData(
            path,
            VirtualNodeType.File,
            SourcePath: file.SourcePath,
            Size: file.Size,
            Mode: VirtualEntry.DefaultMode(VirtualNodeType.File),
            LastModified: lastModified
        ));

    private ResolverSnapshot? CaptureSnapshotAndRequestRefresh()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null || IsStale(snapshot))
            RequestRefresh(forced: false);
        return Volatile.Read(ref _snapshot);
    }

    private void RequestRefresh(bool forced)
    {
        if (Volatile.Read(ref _stopped) != 0)
            return;

        if (forced)
        {
            Volatile.Write(ref _dirty, 1);
            _refreshSignal.Set();
            return;
        }

        if (Volatile.Read(ref _refreshRunning) != 0
            || Volatile.Read(ref _refreshQueued) != 0
            || IsRetryBlocked())
            return;

        if (Interlocked.CompareExchange(ref _refreshQueued, 1, 0) == 0)
            _refreshSignal.Set();
    }

    private bool IsStale(ResolverSnapshot snapshot) =>
        _options.CacheTtl <= TimeSpan.Zero || DateTimeOffset.UtcNow - snapshot.CompletedAt >= _options.CacheTtl;

    private bool IsRetryBlocked() =>
        Volatile.Read(ref _retryAfterUtcTicks) > DateTimeOffset.UtcNow.Ticks;

    private void RefreshLoop()
    {
        try
        {
            while (Volatile.Read(ref _stopped) == 0)
            {
                _refreshSignal.WaitOne();
                if (Volatile.Read(ref _stopped) != 0)
                    return;

                if (Interlocked.CompareExchange(ref _refreshRunning, 1, 0) != 0)
                    continue;

                try
                {
                    RefreshRequestedSnapshots();
                }
                finally
                {
                    if (Interlocked.CompareExchange(ref _refreshRunning, 0, 1) == 1
                        && Volatile.Read(ref _stopped) == 0
                        && (Volatile.Read(ref _dirty) != 0 || Volatile.Read(ref _refreshQueued) != 0))
                        _refreshSignal.Set();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shoko VFS resolver refresh worker stopped unexpectedly.");
        }
    }

    private void RefreshRequestedSnapshots()
    {
        while (Volatile.Read(ref _stopped) == 0)
        {
            bool requested = Interlocked.Exchange(ref _refreshQueued, 0) != 0;
            bool forced = Interlocked.Exchange(ref _dirty, 0) != 0;
            if (!requested && !forced)
                return;
            if (!forced && IsRetryBlocked())
                return;

            bool published = false;
            try
            {
                var build = BuildSnapshot();
                published = PublishSnapshot(build.Snapshot);
                if (published)
                {
                    Volatile.Write(ref _retryAfterUtcTicks, 0);
                    _logger.LogInformation(
                        "Resolver snapshot built in {Elapsed}; Series={SeriesCount}; Mappings={MappingCount}; MappingsWithSourcePath={MappingsWithSourcePathCount}; Models={ModelCount}; ModelsWithTv={TvModelCount}; StandaloneMovieFolders={StandaloneMovieFolderCount}; RootChildren={RootChildCount}; TvChildren={TvChildCount}; MovieChildren={MovieChildCount}; Published={Published}",
                        build.Elapsed,
                        build.SeriesCount,
                        build.MappingCount,
                        build.MappingsWithSourcePathCount,
                        build.ModelCount,
                        build.TvModelCount,
                        build.StandaloneMovieFolderCount,
                        build.RootChildCount,
                        build.TvChildCount,
                        build.MovieChildCount,
                        published
                    );
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _retryAfterUtcTicks, DateTimeOffset.UtcNow.AddSeconds(3).Ticks);
                _logger.LogError(ex, "Failed to build the Shoko VFS resolver snapshot.");
                // ponytail: honor the cooldown so a server outage doesn't spam one log per
                // queued invalidation; the next request after the window retries naturally.
                return;
            }

            // A forced invalidation received during the build gets a complete second pass.
            // The first pass was published before this loop can begin that pass.
            bool dirtyDuringBuild = Interlocked.Exchange(ref _dirty, 0) != 0;
            if (!dirtyDuringBuild)
                return;
            Volatile.Write(ref _dirty, 1);
        }
    }

    private bool PublishSnapshot(ResolverSnapshot snapshot)
    {
        lock (_publishGate)
        {
            if (Volatile.Read(ref _stopped) != 0)
                return false;
            Interlocked.Exchange(ref _snapshot, snapshot);
            return true;
        }
    }

    private SeriesModel BuildModel(SeriesData series)
    {
        var extraFiles = (series.Extras ?? Array.Empty<ExtraFile>())
            .Select(e => new FileModel(e.Name, e.SourcePath, e.Size))
            .ToList();
        var mappings = series.Mappings ?? Array.Empty<EpisodeData>();
        var coordinateCounts = mappings
            .GroupBy(mapping => (mapping.Season, mapping.Episode, mapping.IsVariation))
            .ToDictionary(group => group.Key, group => group.Count());
        int maxEpisode = mappings
            .Where(mapping => mapping.Season >= 0)
            .Select(mapping => mapping.EndEpisode ?? mapping.Episode)
            .DefaultIfEmpty(1)
            .Max();
        int episodePad = Math.Max(2, maxEpisode.ToString().Length);
        var extraPads = mappings
            .Where(mapping => mapping.Season < 0)
            .GroupBy(mapping => mapping.Season)
            .ToDictionary(group => group.Key, group => group.Count() > 9 ? 2 : 1);
        var versionSeeds = coordinateCounts
            .Where(group => group.Value > 1)
            .ToDictionary(group => group.Key, _ => 1);

        TvModel? tv = ShouldGenerateTv(series) ? BuildTvModel(series, mappings, coordinateCounts, versionSeeds, episodePad, extraPads, extraFiles) : null;
        var movies = ShouldGenerateMovie(series) ? BuildMovieModels(series, mappings, coordinateCounts, versionSeeds, extraPads, extraFiles) : [];
        return new SeriesModel(tv, movies);
    }

    private TvModel? BuildTvModel(
        SeriesData series,
        IReadOnlyList<EpisodeData> mappings,
        IReadOnlyDictionary<(int Season, int Episode, bool IsVariation), int> coordinateCounts,
        IReadOnlyDictionary<(int Season, int Episode, bool IsVariation), int> versionSeeds,
        int episodePad,
        IReadOnlyDictionary<int, int> extraPads,
        IReadOnlyList<FileModel> extraFiles)
    {
        var versionCounters = versionSeeds.ToDictionary(pair => pair.Key, pair => pair.Value);
        var seasons = new Dictionary<string, (int Season, List<FileModel> Files)>(StringComparer.Ordinal);

        foreach (var mapping in mappings.OrderBy(mapping => mapping.Season).ThenBy(mapping => mapping.Episode).ThenBy(mapping => mapping.PartIndex ?? 0))
        {
            if (!HasSource(mapping))
                continue;

            var key = (mapping.Season, mapping.Episode, mapping.IsVariation);
            bool hasPeer = coordinateCounts.TryGetValue(key, out var count) && count > 1;
            int? versionIndex = NextVersionIndex(hasPeer, mapping, versionCounters, key);
            int? partIndex = hasPeer ? mapping.PartIndex : null;
            int? partCount = hasPeer ? mapping.PartCount : 1;
            bool omitFileId = mapping.PartCount.GetValueOrDefault() > 1 && mapping.PartIndex.HasValue;
            string fileName = _naming!.IsTvExtraSeason(mapping.Season)
                ? _naming!.FormatExtrasFileName(
                    new ExtrasFileContext(
                        mapping.Season,
                        mapping.Episode,
                        extraPads.GetValueOrDefault(mapping.Season, 1),
                        mapping.EpisodeTitle ?? "",
                        mapping.Extension,
                        partIndex,
                        partCount,
                        versionIndex,
                        mapping.IsVariation
                    )
                )
                : _naming!.FormatEpisodeFileName(
                    new EpisodeFileContext(
                        mapping.Season,
                        mapping.Episode,
                        mapping.EndEpisode,
                        episodePad,
                        mapping.FileId,
                        mapping.Extension,
                        series.DisplayTitle,
                        omitFileId,
                        partIndex,
                        partCount,
                        versionIndex,
                        mapping.IsVariation
                    )
                );

            string seasonName = _naming!.FormatSeasonFolder(mapping.Season);
            if (!seasons.TryGetValue(seasonName, out var season))
            {
                season = (mapping.Season, []);
                seasons.Add(seasonName, season);
            }
            AddFile(season.Files, fileName, mapping.SourcePath!, mapping.Size);
        }

        return seasons.Count == 0
            ? null
            : new TvModel(
                _naming!.FormatSeriesFolder(series.SeriesId, series.DisplayTitle),
                seasons.Values
                    .OrderBy(season => season.Season)
                    .Select(season => new SeasonModel(season.Season, _naming!.FormatSeasonFolder(season.Season), season.Files.ToArray()))
                    .ToArray(),
                extraFiles
            );
    }

    private IReadOnlyList<MovieModel> BuildMovieModels(
        SeriesData series,
        IReadOnlyList<EpisodeData> mappings,
        IReadOnlyDictionary<(int Season, int Episode, bool IsVariation), int> coordinateCounts,
        IReadOnlyDictionary<(int Season, int Episode, bool IsVariation), int> versionSeeds,
        IReadOnlyDictionary<int, int> extraPads,
        IReadOnlyList<FileModel> extraFiles)
    {
        var versionCounters = versionSeeds.ToDictionary(pair => pair.Key, pair => pair.Value);
        var movies = new Dictionary<string, List<FileModel>>(StringComparer.Ordinal);

        foreach (var mapping in mappings.Where(mapping => mapping.IsMain))
        {
            if (!HasSource(mapping))
                continue;

            var key = (mapping.Season, mapping.Episode, mapping.IsVariation);
            bool hasPeer = coordinateCounts.TryGetValue(key, out var count) && count > 1;
            int? versionIndex = NextVersionIndex(hasPeer, mapping, versionCounters, key);
            int? partIndex = hasPeer ? mapping.PartIndex : null;
            int? partCount = hasPeer ? mapping.PartCount : 1;
            bool omitFileId = mapping.PartCount.GetValueOrDefault() > 1 && mapping.PartIndex.HasValue;
            string folderName = _naming!.FormatMovieFolder(mapping.EpisodeId);
            string fileName = _naming.FormatMovieFileName(
                new MovieFileContext(
                    mapping.EpisodeId,
                    mapping.FileId,
                    mapping.Extension,
                    omitFileId,
                    partIndex,
                    partCount,
                    versionIndex,
                    mapping.IsVariation
                )
            );

            if (!movies.TryGetValue(folderName, out var movie))
            {
                movie = [];
                movies.Add(folderName, movie);
            }
            AddFile(movie, fileName, mapping.SourcePath!, mapping.Size);
        }

        if (_options.IncludeMovieExtras && movies.Count > 0)
        {
            var extras = new Dictionary<string, List<FileModel>>(StringComparer.Ordinal);
            foreach (var mapping in mappings.Where(mapping => !mapping.IsMain))
            {
                if (!HasSource(mapping))
                    continue;

                string folderName = _naming!.FormatExtrasFolder(mapping.Season);
                string fileName = _naming.FormatExtrasFileName(
                    new ExtrasFileContext(
                        mapping.Season,
                        mapping.Episode,
                        extraPads.GetValueOrDefault(mapping.Season, 1),
                        mapping.EpisodeTitle ?? "",
                        mapping.Extension,
                        mapping.PartIndex,
                        mapping.PartCount,
                        null,
                        mapping.IsVariation
                    )
                );

                if (!extras.TryGetValue(folderName, out var extra))
                {
                    extra = [];
                    extras.Add(folderName, extra);
                }
                AddFile(extra, fileName, mapping.SourcePath!, mapping.Size);
            }

            return movies
                .OrderBy(movie => movie.Key, StringComparer.Ordinal)
                .Select(movie => new MovieModel(
                    movie.Key,
                    movie.Value.ToArray(),
                    extras.Select(extra => new ExtraDirectoryModel(extra.Key, extra.Value.ToArray())).ToArray(),
                    extraFiles
                ))
                .ToArray();
        }

        return movies
            .OrderBy(movie => movie.Key, StringComparer.Ordinal)
            .Select(movie => new MovieModel(movie.Key, movie.Value.ToArray(), Array.Empty<ExtraDirectoryModel>(), extraFiles))
            .ToArray();
    }

    private static int? NextVersionIndex(
        bool hasPeer,
        EpisodeData mapping,
        IDictionary<(int Season, int Episode, bool IsVariation), int> counters,
        (int Season, int Episode, bool IsVariation) key)
    {
        if (!hasPeer || mapping.PartIndex.HasValue || !counters.TryGetValue(key, out var value))
            return null;

        counters[key] = value + 1;
        return value;
    }

    private static bool HasSource(EpisodeData mapping) => !string.IsNullOrWhiteSpace(mapping.SourcePath);

    private bool ShouldGenerateTv(SeriesData series) => series.IsMovie ? _options.MoviesAsTv : _options.Shows;

    private bool ShouldGenerateMovie(SeriesData series) => series.IsMovie && _options.StandaloneMovies;

    private static string[] NormalizePath(string path) =>
        (path ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static List<RootChild> DistinctRootChildren(IEnumerable<RootChild> children)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return children.Where(child => seen.Add(child.Name)).OrderBy(child => child.Name, StringComparer.Ordinal).ToList();
    }

    private static void AddFile(List<FileModel> files, string name, string sourcePath, long size)
    {
        // ponytail: O(n²) filename dedupe; use per-directory hash sets if virtual directories become large.
        if (files.Any(file => string.Equals(file.Name, name, StringComparison.Ordinal)))
            return;
        files.Add(new FileModel(name, sourcePath, Math.Max(0, size)));
    }

    private enum RootChildKind
    {
        TvRoot,
        MovieRoot,
        TvSeries,
        MovieFolder,
    }

    private sealed record RootChild(string Name, RootChildKind Kind, SeriesModel? Series = null, MovieModel? Movie = null);

    private sealed class ResolverSnapshot(VirtualTreeSnapshot tree, DateTimeOffset completedAt)
    {
        public VirtualTreeSnapshot Tree { get; } = tree;
        public DateTimeOffset CompletedAt { get; } = completedAt;
    }

    private sealed record SnapshotBuildResult(
        ResolverSnapshot Snapshot,
        TimeSpan Elapsed,
        int SeriesCount,
        int MappingCount,
        int MappingsWithSourcePathCount,
        int ModelCount,
        int TvModelCount,
        int StandaloneMovieFolderCount,
        int RootChildCount,
        int TvChildCount,
        int MovieChildCount);

    private sealed class SeriesModel(TvModel? tv, IReadOnlyList<MovieModel> movies)
    {
        public TvModel? Tv { get; } = tv;
        public IReadOnlyList<MovieModel> Movies { get; } = movies;
        public string? SeriesFolderName => Tv?.SeriesFolderName;
    }

    private sealed class TvModel(string seriesFolderName, IReadOnlyList<SeasonModel> seasons, IReadOnlyList<FileModel> extraFiles)
    {
        public string SeriesFolderName { get; } = seriesFolderName;
        public IReadOnlyList<SeasonModel> Seasons { get; } = seasons;
        public IReadOnlyList<FileModel> ExtraFiles { get; } = extraFiles;
    }

    private sealed class SeasonModel(int season, string name, IReadOnlyList<FileModel> files)
    {
        public int Season { get; } = season;
        public string Name { get; } = name;
        public IReadOnlyList<FileModel> Files { get; } = files;
    }

    private sealed class MovieModel(string folderName, IReadOnlyList<FileModel> files, IReadOnlyList<ExtraDirectoryModel> extras, IReadOnlyList<FileModel> extraFiles)
    {
        public string FolderName { get; } = folderName;
        public IReadOnlyList<FileModel> Files { get; } = files;
        public IReadOnlyList<ExtraDirectoryModel> Extras { get; } = extras;
        public IReadOnlyList<FileModel> ExtraFiles { get; } = extraFiles;
    }

    private sealed class ExtraDirectoryModel(string name, IReadOnlyList<FileModel> files)
    {
        public string Name { get; } = name;
        public IReadOnlyList<FileModel> Files { get; } = files;
    }

    private sealed record FileModel(string Name, string SourcePath, long Size);
}
