using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Resolvers.Fin;

namespace Shoko.VFS.FUSE.Tests.Resolvers.Fin;

public sealed class FinPathProjectorTests
{
    [Fact]
    public void TvNormal_EmitsCanonicalSymlinkAndExplicitParents()
    {
        var profile = Profile();
        var imported = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));
        var input = Input(
            admitted: new FinAdmittedFile("/media/source.mkv", 99, 123, 456),
            show: new FinShowProjectionInput(10, "My Show", "TMDB Show", 3),
            episode: new FinEpisodeProjectionInput(20, 30, 7, 2, FinEpisodeType.Episode, false, false, false, null),
            createdAt: imported.AddDays(1),
            importedAt: imported
        );

        var entries = FinPathProjector.Project(profile, input);
        var expectedShow = "My Show [Shoko Series=10]";
        var expectedSeason = expectedShow + "/Season 02";
        var expectedFile = "My Show S02E007 [Shoko Series=123] [Shoko File=99].mkv";

        Assert.Equal(
            new[]
            {
                profile.VirtualRootSegment,
                profile.VirtualRootSegment + "/" + expectedShow,
                profile.VirtualRootSegment + "/" + expectedSeason,
            },
            entries.Where(entry => entry.NodeType == VirtualNodeType.Directory).Select(entry => entry.Path)
        );
        var symlink = Assert.Single(entries, entry => entry.NodeType == VirtualNodeType.Symlink);
        Assert.Equal(profile.VirtualRootSegment + "/" + expectedSeason + "/" + expectedFile, symlink.Path);
        Assert.Equal(VirtualNodeType.Symlink, symlink.NodeType);
        Assert.Equal("/media/source.mkv", symlink.SymlinkTarget);
        Assert.Equal(imported, symlink.LastModified);
    }

    [Fact]
    public void TvSpecial_UsesSeason00AndSuppliedEpisodePadding()
    {
        var input = Input(
            show: new FinShowProjectionInput(10, "Show", null, 4),
            episode: new FinEpisodeProjectionInput(20, 30, 5, 9, FinEpisodeType.Special, true, false, false, null)
        );

        var symlink = SingleSymlink(FinPathProjector.Project(Profile(), input));

        Assert.Contains("/Season 00/", symlink.Path);
        Assert.EndsWith("/Show S00E0005 [Shoko Series=123] [Shoko File=99].mkv", symlink.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Titles_UseSourcesFallbacksSanitizationAndCanonicalLongCutoff()
    {
        var profile = Profile();
        var sanitized = Input(
            show: new FinShowProjectionInput(1, "A  B__C/Δ", "TMDB", 2),
            episode: new FinEpisodeProjectionInput(2, 3, 1, 1, FinEpisodeType.Episode, false, true, false, "Extra!  Title")
        );
        var sanitizedLinks = SymlinkPaths(FinPathProjector.Project(profile, sanitized));
        Assert.All(sanitizedLinks, path =>
        {
            Assert.Contains($"{profile.VirtualRootSegment}/A B_C_ [Shoko Series=1]/", path, StringComparison.Ordinal);
            Assert.Contains("Extra_ Title", path, StringComparison.Ordinal);
        });

        var tmdbFallback = Input(show: new FinShowProjectionInput(1, null, "TMDB Show", 2));
        Assert.Contains($"{profile.VirtualRootSegment}/TMDB Show [Shoko Series=1]/", SingleSymlink(FinPathProjector.Project(profile, tmdbFallback)).Path, StringComparison.Ordinal);

        var seriesFallback = Input(show: new FinShowProjectionInput(1, null, null, 2));
        Assert.Contains($"{profile.VirtualRootSegment}/Series [Shoko Series=1]/", SingleSymlink(FinPathProjector.Project(profile, seriesFallback)).Path, StringComparison.Ordinal);

        var movieFallback = Input(
            isMovieLibrary: true,
            show: new FinShowProjectionInput(1, null, null, 2),
            episode: new FinEpisodeProjectionInput(2, 3, 1, 1, FinEpisodeType.Episode, false, false, false, null)
        );
        Assert.Contains($"{profile.VirtualRootSegment}/Movie [Shoko Series=2] [Shoko Episode=3]/", SingleSymlink(FinPathProjector.Project(profile, movieFallback)).Path, StringComparison.Ordinal);

        string longTitle = new string('A', 63) + " B";
        var longInput = Input(show: new FinShowProjectionInput(1, longTitle, null, 2));
        Assert.Contains($"{profile.VirtualRootSegment}/{new string('A', 63)}… [Shoko Series=1]/", SingleSymlink(FinPathProjector.Project(profile, longInput)).Path, StringComparison.Ordinal);
    }

    [Fact]
    public void TvMultipart_UsesCanonicalIdsAndDotPartSuffixOnly()
    {
        var input = Input(
            admitted: new FinAdmittedFile("/media/part2.mkv", 102, 123, 456),
            multipartParts: [new FinMultipartPart(101, 1), new FinMultipartPart(102, 2)]
        );

        var path = SingleSymlink(FinPathProjector.Project(Profile(), input)).Path;

        Assert.EndsWith("[Shoko Series=123] [Shoko File=101,102].pt2.mkv", path, StringComparison.Ordinal);
        Assert.DoesNotContain("-pt", path);
        Assert.DoesNotContain("-dup", path);
        Assert.DoesNotContain("[variation]", path);
    }

    [Fact]
    public void MovieNormal_UsesSeasonAndEpisodeFolderWithOptionalDetails()
    {
        var input = Input(
            isMovieLibrary: true,
            admitted: new FinAdmittedFile("/media/movie.mkv", 88, 77, 66),
            show: new FinShowProjectionInput(10, "Movie Show", null, 2),
            episode: new FinEpisodeProjectionInput(55, 66, 1, 1, FinEpisodeType.Episode, false, false, false, null),
            releaseGroup: new FinReleaseGroup(1, "Grp/Name", "Long Name"),
            resolution: "1920x1080"
        );

        var entries = FinPathProjector.Project(
            Profile(),
            input,
            new FinProjectorOptions { AddReleaseGroup = true, AddResolution = true }
        );
        var symlink = SingleSymlink(entries);

        Assert.Equal($"{Profile().VirtualRootSegment}/Movie Show [Shoko Series=55] [Shoko Episode=66]/Movie [Grp_Name] [1920x1080] [Shoko Series=77] [Shoko File=88].mkv", symlink.Path);
        Assert.Equal(VirtualNodeType.Symlink, symlink.NodeType);
        Assert.DoesNotContain(".pt", symlink.Path);
    }

    [Fact]
    public void TvExtras_UseLiteralFoldersAndThemeTrailerSwitches()
    {
        var profile = Profile();
        var extra = Input(
            episode: new FinEpisodeProjectionInput(20, 30, 1, 2, FinEpisodeType.Extra, false, true, false, "Extra"),
            extraType: null
        );
        var extraPaths = SymlinkPaths(FinPathProjector.Project(profile, extra));
        Assert.Equal(
            new[]
            {
                $"{profile.VirtualRootSegment}/Show [Shoko Series=1]/extras/Extra [Shoko Series=123] [Shoko File=99].mkv",
                $"{profile.VirtualRootSegment}/Show [Shoko Series=1]/Season 02/extras/Extra [Shoko Series=123] [Shoko File=99].mkv",
            },
            extraPaths
        );

        var theme = WithExtraType(extra, FinExtraType.ThemeVideo);
        var themePaths = SymlinkPaths(FinPathProjector.Project(
            profile,
            theme,
            new FinProjectorOptions { AddCreditsAsThemeVideos = true, AddCreditsAsSpecialFeatures = true }
        ));
        Assert.Equal(4, themePaths.Length);
        Assert.All(themePaths, path => Assert.True(path.Contains("/backdrops/") || path.Contains("/extras/")));

        var trailer = WithExtraType(extra, FinExtraType.Trailer);
        Assert.Empty(FinPathProjector.Project(profile, trailer, new FinProjectorOptions { AddTrailers = false }));

        var expectedFolders = new Dictionary<FinExtraType, string>
        {
            [FinExtraType.ThemeSong] = "theme-music",
            [FinExtraType.BehindTheScenes] = "behind the scenes",
            [FinExtraType.DeletedScene] = "deleted scenes",
            [FinExtraType.Clip] = "clips",
            [FinExtraType.Interview] = "interviews",
            [FinExtraType.Scene] = "scenes",
            [FinExtraType.Sample] = "samples",
            [FinExtraType.Featurette] = "extras",
            [FinExtraType.Other] = "extras",
        };
        foreach (var (type, folder) in expectedFolders)
            Assert.All(SymlinkPaths(FinPathProjector.Project(profile, WithExtraType(extra, type))), path =>
                Assert.Contains($"/{folder}/", path, StringComparison.Ordinal));
    }

    [Fact]
    public void MovieExtras_FanOutAcrossAvailableEpisodeIds()
    {
        var input = Input(
            isMovieLibrary: true,
            episode: new FinEpisodeProjectionInput(55, 66, 1, 1, FinEpisodeType.Extra, false, true, false, "Making Of"),
            extraType: FinExtraType.Featurette,
            availableMovieEpisodeIds: [66, 77]
        );

        var paths = SymlinkPaths(FinPathProjector.Project(Profile(), input));

        Assert.Equal(2, paths.Length);
        Assert.Equal(
            new[]
            {
                $"{Profile().VirtualRootSegment}/Show [Shoko Series=55] [Shoko Episode=66]/extras/Making Of [Shoko Series=123] [Shoko File=99].mkv",
                $"{Profile().VirtualRootSegment}/Show [Shoko Series=55] [Shoko Episode=77]/extras/Making Of [Shoko Series=123] [Shoko File=99].mkv",
            },
            paths
        );
    }

    [Fact]
    public void ProjectedDirectoriesUseEarliestTimestampAndPathsAreUnique()
    {
        var profile = Profile();
        var late = Input(createdAt: new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero));
        var early = Input(
            admitted: new FinAdmittedFile("/media/early.mkv", 100, 123, 456),
            createdAt: new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero)
        );

        var entries = FinPathProjector.Project(profile, [late, early, early]);

        Assert.Equal(entries.Count, entries.Select(entry => entry.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, entries.Count(entry => entry.NodeType == VirtualNodeType.Symlink));
        var directory = entries.Single(entry => entry.Path == $"{profile.VirtualRootSegment}/Show [Shoko Series=1]/Season 01");
        Assert.Equal(early.CreatedAt, directory.LastModified);
    }

    [Fact]
    public void EveryProjectedMediaNodeIsASymlinkWithTheAdmittedSourceTarget()
    {
        var entries = FinPathProjector.Project(Profile(), Input());

        Assert.NotEmpty(entries);
        Assert.All(entries.Where(entry => entry.NodeType != VirtualNodeType.Directory), entry =>
        {
            Assert.Equal(VirtualNodeType.Symlink, entry.NodeType);
            Assert.Equal("/media/source.mkv", entry.SymlinkTarget);
        });
    }

    private static FinLibraryProfile Profile() =>
        new(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"));

    private static FinProjectionInput Input(
        bool isMovieLibrary = false,
        FinAdmittedFile? admitted = null,
        FinShowProjectionInput? show = null,
        FinEpisodeProjectionInput? episode = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? importedAt = null,
        string sourceExtension = ".mkv",
        FinExtraType? extraType = null,
        IReadOnlyList<int>? availableMovieEpisodeIds = null,
        IReadOnlyList<FinMultipartPart>? multipartParts = null,
        FinReleaseGroup? releaseGroup = null,
        string? resolution = null) => new(
        admitted ?? new FinAdmittedFile("/media/source.mkv", 99, 123, 456),
        show ?? new FinShowProjectionInput(1, "Show", null, 2),
        episode ?? new FinEpisodeProjectionInput(2, 3, 1, 1, FinEpisodeType.Episode, false, false, false, null),
        isMovieLibrary,
        createdAt ?? new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        importedAt,
        sourceExtension,
        extraType,
        availableMovieEpisodeIds,
        multipartParts,
        releaseGroup,
        resolution
    );

    private static VirtualTreeEntryData SingleSymlink(IReadOnlyList<VirtualTreeEntryData> entries) =>
        Assert.Single(entries, entry => entry.NodeType == VirtualNodeType.Symlink);

    private static string[] SymlinkPaths(IReadOnlyList<VirtualTreeEntryData> entries) =>
        entries.Where(entry => entry.NodeType == VirtualNodeType.Symlink).Select(entry => entry.Path).ToArray();

    private static FinProjectionInput WithExtraType(FinProjectionInput input, FinExtraType? extraType) =>
        new(
            input.AdmittedFile,
            input.Show,
            input.Episode,
            input.IsMovieLibrary,
            input.CreatedAt,
            input.ImportedAt,
            input.SourceExtension,
            extraType,
            input.AvailableMovieEpisodeIds,
            input.MultipartParts,
            input.ReleaseGroup,
            input.Resolution
        );
}
