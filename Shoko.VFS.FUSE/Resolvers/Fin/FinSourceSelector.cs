namespace Shoko.VFS.FUSE.Resolvers.Fin;

/// <summary>Pure port of Shokofin's managed-folder source admission rules.</summary>
public static class FinSourceSelector
{
    public static IReadOnlyList<FinAdmittedFile> GetFilesForManagedFolders(
        FinLibraryProfile profile,
        IReadOnlyList<FinMediaFolderMapping> mediaMappings,
        IReadOnlyList<FinRawFile> rawFiles,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(mediaMappings);
        ArgumentNullException.ThrowIfNull(rawFiles);
        ArgumentNullException.ThrowIfNull(fileExists);

        var singleSeriesIds = new HashSet<int>();
        var deferred = new List<DeferredFile>();
        var admitted = new List<FinAdmittedFile>();

        foreach (var mapping in mediaMappings)
        {
            foreach (var file in rawFiles)
            {
                if (file.CrossReferences.Count == 0)
                    continue;

                var location = file.Locations.FirstOrDefault(candidate =>
                    candidate.ManagedFolderId == mapping.ManagedFolderId
                    && (mapping.ManagedFolderSubPath.Length == 0
                        || candidate.RelativePath.StartsWith(mapping.ManagedFolderSubPath)));
                if (location is null)
                    continue;

                string? sourcePath = null;
                foreach (var mediaFolderPath in mapping.MediaFolderPaths)
                {
                    var candidate = Path.Join(
                        mediaFolderPath,
                        location.RelativePath[mapping.ManagedFolderSubPath.Length..]
                    );
                    if (fileExists(candidate))
                    {
                        sourcePath = candidate;
                        break;
                    }
                }

                if (sourcePath is null)
                    continue;

                var identities = EligibleSeries(file.CrossReferences);
                int distinctSeriesCount = identities.Select(identity => identity.SeriesId).Distinct().Count();
                if (distinctSeriesCount == 1)
                {
                    var identity = identities[0];
                    singleSeriesIds.Add(identity.SeriesId);
                    admitted.Add(new FinAdmittedFile(sourcePath, file.FileId, identity.SeriesId, identity.AniDbId));
                }
                else if (identities.Count > 1)
                {
                    deferred.Add(new DeferredFile(file.FileId, sourcePath, identities));
                }
            }
        }

        foreach (var file in deferred)
        {
            var admittedSeriesIds = new HashSet<int>();
            foreach (var identity in file.Identities)
            {
                if ((singleSeriesIds.Contains(identity.SeriesId)
                        || profile.MultiSeriesAniDbAllowlist.Contains(identity.AniDbId))
                    && admittedSeriesIds.Add(identity.SeriesId))
                    admitted.Add(new FinAdmittedFile(file.SourcePath, file.FileId, identity.SeriesId, identity.AniDbId));
            }
        }

        return admitted.AsReadOnly();
    }

    private static List<SeriesIdentity> EligibleSeries(
        IReadOnlyList<FinRawFileCrossReference> crossReferences)
    {
        var identities = new List<SeriesIdentity>();
        var seen = new HashSet<(int SeriesId, int AniDbId)>();
        foreach (var crossReference in crossReferences)
        {
            if (!crossReference.ShokoSeriesId.HasValue || !crossReference.AllEpisodesHaveShokoId)
                continue;

            var identity = new SeriesIdentity(crossReference.ShokoSeriesId.Value, crossReference.AniDbId);
            if (seen.Add((identity.SeriesId, identity.AniDbId)))
                identities.Add(identity);
        }

        return identities;
    }

    private sealed record SeriesIdentity(int SeriesId, int AniDbId);

    private sealed record DeferredFile(int FileId, string SourcePath, IReadOnlyList<SeriesIdentity> Identities);
}
