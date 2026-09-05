using Shoko.VFS.FUSE.Resolvers.Fin;

namespace Shoko.VFS.FUSE.Tests.Resolvers.Fin;

public sealed class FinSourceSelectorTests
{
    [Fact]
    public void LibraryRoot_AlwaysUsesCanonicalGuidOnlyRoot()
    {
        var libraryId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var profile = new FinLibraryProfile(libraryId);

        Assert.Equal(libraryId.ToString(), profile.VirtualRootSegment);
        Assert.Equal(Path.Join("/vfs", libraryId.ToString()), profile.GetVirtualRootPath("/vfs"));
    }

    [Fact]
    public void MatchingLocation_UsesFirstExistingOrderedMediaRoot()
    {
        var mapping = new FinMediaFolderMapping(Guid.NewGuid(), 7, "anime", ["/first", "/second", "/third"]);
        var file = RawFile(100, "anime/episode.mkv", Reference(10, 1000));
        var observed = new List<string>();
        string selected = Path.Join("/second", "/episode.mkv");

        var result = FinSourceSelector.GetFilesForManagedFolders(
            new FinLibraryProfile(mapping.LibraryId, multiSeriesAniDbAllowlist: []),
            [mapping],
            [file],
            path =>
            {
                observed.Add(path);
                return path == selected;
            }
        );

        Assert.Equal(new[] { Path.Join("/first", "/episode.mkv"), selected }, observed);
        var admitted = Assert.Single(result);
        Assert.Equal(selected, admitted.SourcePath);
        Assert.Equal(100, admitted.FileId);
        Assert.Equal(10, admitted.ShokoSeriesId);
        Assert.Equal(1000, admitted.AniDbId);
    }

    [Fact]
    public void FilesWithoutValidShokoReferencesOrSourceAreSkipped()
    {
        var mapping = new FinMediaFolderMapping(Guid.NewGuid(), 7, "", ["/media"]);
        var noReferences = RawFile(1, "no-ref.mkv");
        var nonShoko = RawFile(2, "non-shoko.mkv", Reference(null, 20));
        var unresolvedEpisode = RawFile(3, "unresolved.mkv", Reference(30, 30, allEpisodesHaveShokoId: false));
        var missingSource = RawFile(4, "missing.mkv", Reference(40, 40));

        var result = FinSourceSelector.GetFilesForManagedFolders(
            new FinLibraryProfile(mapping.LibraryId, multiSeriesAniDbAllowlist: []),
            [mapping],
            [noReferences, nonShoko, unresolvedEpisode, missingSource],
            _ => false
        );

        Assert.Empty(result);
    }

    [Fact]
    public void DeferredSharedFile_IsAdmittedForSeriesMadePresentBySingleFile()
    {
        var mapping = new FinMediaFolderMapping(Guid.NewGuid(), 7, "", ["/media"]);
        var shared = RawFile(1, "shared.mkv", Reference(10, 100), Reference(20, 200));
        var single = RawFile(2, "single.mkv", Reference(20, 200));

        var result = FinSourceSelector.GetFilesForManagedFolders(
            new FinLibraryProfile(mapping.LibraryId, multiSeriesAniDbAllowlist: []),
            [mapping],
            [shared, single],
            _ => true
        );

        Assert.Equal(
            new[]
            {
                new FinAdmittedFile("/media/single.mkv", 2, 20, 200),
                new FinAdmittedFile("/media/shared.mkv", 1, 20, 200),
            },
            result
        );
    }

    [Fact]
    public void DeferredSharedFile_IsExcludedWithoutPresentOrAllowlistedSeries()
    {
        var mapping = new FinMediaFolderMapping(Guid.NewGuid(), 7, "", ["/media"]);
        var shared = RawFile(1, "shared.mkv", Reference(10, 100), Reference(20, 200));

        var result = FinSourceSelector.GetFilesForManagedFolders(
            new FinLibraryProfile(mapping.LibraryId, multiSeriesAniDbAllowlist: []),
            [mapping],
            [shared],
            _ => true
        );

        Assert.Empty(result);
    }

    [Fact]
    public void DefaultAllowlist_AdmitsAniDb3651SharedSeries()
    {
        var mapping = new FinMediaFolderMapping(Guid.NewGuid(), 7, "", ["/media"]);
        var shared = RawFile(1, "shared.mkv", Reference(10, 3651), Reference(20, 200));

        var result = FinSourceSelector.GetFilesForManagedFolders(
            new FinLibraryProfile(mapping.LibraryId),
            [mapping],
            [shared],
            _ => true
        );

        Assert.Equal([new FinAdmittedFile("/media/shared.mkv", 1, 10, 3651)], result);
    }

    [Fact]
    public void DeferredSharedFile_EmitsAllowlistedSeriesInCrossReferenceOrder()
    {
        var mapping = new FinMediaFolderMapping(Guid.NewGuid(), 7, "", ["/media"]);
        var shared = RawFile(1, "shared.mkv", Reference(30, 300), Reference(10, 100), Reference(20, 200));
        var profile = new FinLibraryProfile(mapping.LibraryId, multiSeriesAniDbAllowlist: [300, 100, 200]);

        var result = FinSourceSelector.GetFilesForManagedFolders(profile, [mapping], [shared], _ => true);

        Assert.Equal(
            new[]
            {
                new FinAdmittedFile("/media/shared.mkv", 1, 30, 300),
                new FinAdmittedFile("/media/shared.mkv", 1, 10, 100),
                new FinAdmittedFile("/media/shared.mkv", 1, 20, 200),
            },
            result
        );
    }

    [Fact]
    public void DeferredSharedFile_DeduplicatesBySeriesAndAniDbTupleBeforeAllowlistFiltering()
    {
        var mapping = new FinMediaFolderMapping(Guid.NewGuid(), 7, "", ["/media"]);
        var shared = RawFile(1, "shared.mkv", Reference(10, 100), Reference(20, 200), Reference(10, 3651));

        var result = FinSourceSelector.GetFilesForManagedFolders(
            new FinLibraryProfile(mapping.LibraryId),
            [mapping],
            [shared],
            _ => true
        );

        // (10, 100) and (10, 3651) are distinct canonical tuples. Only the
        // latter qualifies by AniDB, but the final selector still emits one
        // link for the distinct Shoko series.
        Assert.Equal([new FinAdmittedFile("/media/shared.mkv", 1, 10, 3651)], result);
    }

    [Fact]
    public void SourceSelection_OnlyObservesInjectedCandidatePaths()
    {
        var mapping = new FinMediaFolderMapping(Guid.NewGuid(), 7, "folder", ["/one", "/two"]);
        var noReferences = RawFile(1, "folder/no-ref.mkv");
        var file = RawFile(2, "folder/file.mkv", Reference(10, 100));
        var observed = new List<string>();

        var result = FinSourceSelector.GetFilesForManagedFolders(
            new FinLibraryProfile(mapping.LibraryId, multiSeriesAniDbAllowlist: []),
            [mapping],
            [noReferences, file],
            path =>
            {
                observed.Add(path);
                return path == Path.Join("/two", "/file.mkv");
            }
        );

        Assert.Equal(new[] { Path.Join("/one", "/file.mkv"), Path.Join("/two", "/file.mkv") }, observed);
        Assert.Equal("/two/file.mkv", Assert.Single(result).SourcePath);
    }

    private static FinRawFile RawFile(int fileId, string relativePath, params FinRawFileCrossReference[] references) =>
        new(fileId, [new FinRawLocation(7, relativePath)], references);

    private static FinRawFileCrossReference Reference(int? seriesId, int aniDbId, bool allEpisodesHaveShokoId = true) =>
        new(seriesId, aniDbId, allEpisodesHaveShokoId);
}
