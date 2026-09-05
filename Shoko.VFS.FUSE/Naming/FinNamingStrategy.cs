namespace Shoko.VFS.FUSE.Naming;

/// <summary>Fin (Jellyfin) naming strategy. Matches Shokofin's VFS conventions exactly.</summary>
public sealed class FinNamingStrategy : IPathNamingStrategy
{
    private const int NameCutOff = 64;

    public string Consumer => "fin";

    public string? TvRootFolderName => null;

    public string? MovieRootFolderName => null;

    public bool IsTvExtraSeason(int season) => false;

    public string FormatSeriesFolder(int seriesId, string? title)
    {
        var name = TruncateName(title ?? $"Series {seriesId}");
        return $"{name} [ShokoSeries={seriesId}]";
    }

    public string FormatSeasonFolder(int seasonNumber) => $"Season {seasonNumber:D2}";

    public string FormatEpisodeFileName(EpisodeFileContext ctx)
    {
        string name = ctx.EndEpisode.HasValue && ctx.EndEpisode != ctx.Episode
            ? $"S{ctx.Season:D2}E{ctx.Episode:D2}-E{ctx.EndEpisode.Value:D2}"
            : $"S{ctx.Season:D2}E{ctx.Episode:D2}";
        if (ctx.SeriesTitle is not null)
            name = $"{ctx.SeriesTitle} {name}";

        return FormatFileName(name, $"[ShokoFile={ctx.FileId}]", ctx.Extension, ctx.OmitFileId, ctx.PartIndex, ctx.PartCount, ctx.VersionIndex, ctx.IsVariation);
    }

    public string FormatMovieFileName(MovieFileContext ctx) =>
        FormatFileName(
            $"Movie [ShokoEpisode={ctx.EpisodeId}]",
            $"[ShokoFile={ctx.FileId}]",
            ctx.Extension,
            ctx.OmitFileId,
            ctx.PartIndex,
            ctx.PartCount,
            ctx.VersionIndex,
            ctx.IsVariation
        );

    public string FormatMovieFolder(int episodeId) => $"Movie {episodeId}";

    public string FormatExtrasFolder(int season) => throw new NotSupportedException();

    public string FormatExtrasFileName(ExtrasFileContext ctx) => throw new NotSupportedException();

    private static string FormatFileName(
        string baseName,
        string fileIdSuffix,
        string extension,
        bool omitFileId,
        int? partIndex,
        int? partCount,
        int? versionIndex,
        bool isVariation)
    {
        int totalParts = partCount.GetValueOrDefault();
        string name = baseName;
        if (totalParts > 1)
            name += $"-pt{partIndex}";
        else if (versionIndex.HasValue)
            name += $"-dup{versionIndex.Value}";

        if (!omitFileId)
            name += $" {fileIdSuffix}";
        if (isVariation && totalParts <= 1)
            name += "[variation]";

        return name + extension;
    }

    private static string TruncateName(string name) => name.Length > NameCutOff ? name[..NameCutOff] : name;
}
