using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.VFS.FUSE.Host.Models;

// Wire DTOs for the Shoko Server API v3 (https://github.com/ShokoDAVinci/ShokoServer,
// Shoko.Server/API/v3). Property names match the server's Newtonsoft JSON output 1:1
// (PascalCase, DefaultContractResolver). Only the fields the host needs are mapped;
// unknown server fields are ignored on deserialization.
//
// AnimeType/EpisodeType/TitleType are the Shoko.Abstractions enums: the server's v3 DTOs
// use those exact types with StringEnumConverter, so the wire values match 1:1.
// DropFolderType is defined locally because the server's v3 ManagedFolder DTO declares
// its own enum whose zero member is named "None" (Abstractions' is "Excluded").
// PartialDateOnly stays local (Shoko.VFS.FUSE.Host.Models): the server's Abstractions
// struct is get-only, so Newtonsoft cannot deserialize the POCO date shape into it.

namespace Shoko.VFS.FUSE.Host.Api.Models;

/// <summary>Response of <c>POST /api/auth</c> — <c>{ "apikey": "..." }</c>.</summary>
public sealed class AuthResponse
{
    public string? Apikey { get; set; }
}

/// <summary>Server-side pagination wrapper (<c>ListResult&lt;T&gt;</c>).</summary>
public sealed class ListResult<T>
{
    public int Total { get; set; }

    public IReadOnlyList<T> List { get; set; } = [];
}

#region Managed Folder (Models/Shoko/ManagedFolder.cs)

/// <summary>
/// Wire copy of <c>Shoko.Server.API.v3.Models.Shoko.DropFolderType</c> — member names match
/// the server's StringEnumConverter output (<c>None</c>/<c>Source</c>/<c>Destination</c>/<c>Both</c>).
/// The values are interchangeable with <c>Shoko.Abstractions.Video.Enums.DropFolderType</c>
/// (Excluded = 0).
/// </summary>
[Flags]
[JsonConverter(typeof(StringEnumConverter))]
public enum DropFolderType
{
    None = 0,
    Source = 1,
    Destination = 2,
    Both = Source | Destination,
}

/// <summary><c>GET /api/v3/ManagedFolder</c> list entry / <c>GET /api/v3/ManagedFolder/{id}</c>.</summary>
public sealed class ManagedFolderDto
{
    public int ID { get; set; }

    public string Name { get; set; } = "";

    public string Path { get; set; } = "";

    public bool WatchForNewFiles { get; set; }

    public DropFolderType DropFolderType { get; set; }

    public long FileSize { get; set; }
}

#endregion

#region Series (Models/Shoko/Series.cs + Models/AniDB/AnidbAnime.cs)

/// <summary>
/// <c>GET /api/v3/Series</c> list entry and <c>GET /api/v3/Series/{id}</c>.
/// <c>AniDB</c> is present with <c>includeDataFrom=AniDB</c>; <c>TMDB</c> with <c>includeDataFrom=TMDB</c>.
/// </summary>
public sealed class ShokoSeriesDto
{
    public SeriesIdsDto IDs { get; set; } = new();

    /// <summary>The server's preferred title (overrides → naming settings → main title).</summary>
    public string Name { get; set; } = "";

    public AnidbAnimeDto? AniDB { get; set; }

    public TmdbSeriesDataDto? TMDB { get; set; }
}

public sealed class SeriesIdsDto
{
    public int ID { get; set; }

    public int AniDB { get; set; }
}

public sealed class TmdbSeriesDataDto
{
    public List<TmdbShowDto> Shows { get; set; } = [];

    public List<TmdbMovieDto> Movies { get; set; } = [];
}

/// <summary><c>Models/AniDB/AnidbAnime.cs</c> — <c>Series.AniDB</c> payload.</summary>
public sealed class AnidbAnimeDto
{
    public int ID { get; set; }

    public AnimeType Type { get; set; }

    public string Title { get; set; } = "";

    public List<TitleDto>? Titles { get; set; }

    public PartialDateOnly? AirDate { get; set; }

    public PartialDateOnly? EndDate { get; set; }
}

#endregion

#region Episode (Models/Shoko/Episode.cs + Models/AniDB/AnidbEpisode.cs)

/// <summary>
/// <c>GET /api/v3/Series/{id}/Episode</c> entry. Built with
/// <c>includeDataFrom=AniDB,TMDB&amp;includeFiles=true&amp;includeXRefs=true</c> here, so
/// <c>AniDB</c>, <c>TMDB</c>, <c>Files</c> and <c>CrossReferences</c> are populated.
/// </summary>
public sealed class ShokoEpisodeDto
{
    public EpisodeIdsDto IDs { get; set; } = new();

    /// <summary>The server's preferred title (override → preferred → default).</summary>
    public string Name { get; set; } = "";

    /// <summary>Bare DTO episode number (present without <c>includeDataFrom</c>).</summary>
    public int IndexNumber { get; set; }

    public bool IsHidden { get; set; }

    public AnidbEpisodeDto? AniDB { get; set; }

    public TmdbEpisodeDataDto? TMDB { get; set; }

    public List<FileDto>? Files { get; set; }

    /// <summary><c>includeXRefs=true</c>: episode cross-references (first series group).</summary>
    public List<FileEpisodeIdsDto>? CrossReferences { get; set; }
}

public sealed class EpisodeIdsDto
{
    public int ID { get; set; }

    public int ParentSeries { get; set; }

    public TmdbEpisodeIdListDto TMDB { get; set; } = new();
}

public sealed class TmdbEpisodeIdListDto
{
    public List<int> Episode { get; set; } = [];

    public List<int> Show { get; set; } = [];

    public List<int> Movie { get; set; } = [];
}

/// <summary><c>Models/AniDB/AnidbEpisode.cs</c> — <c>Episode.AniDB</c> payload.</summary>
public sealed class AnidbEpisodeDto
{
    public int ID { get; set; }

    public int AnimeID { get; set; }

    public EpisodeType Type { get; set; }

    public int EpisodeNumber { get; set; }

    public DateOnly? AirDate { get; set; }

    public string Title { get; set; } = "";

    public List<TitleDto>? Titles { get; set; }
}

/// <summary><c>Episode.TMDB</c> block (<c>includeDataFrom=TMDB</c>).</summary>
public sealed class TmdbEpisodeDataDto
{
    /// <summary>
    /// TMDB episodes linked to the Shoko episode. When the show has a preferred
    /// alternate ordering the server already substitutes those coordinates into
    /// <c>SeasonNumber</c>/<c>EpisodeNumber</c>/<c>AlternateOrderingID</c>;
    /// otherwise they are the default-ordering coordinates.
    /// </summary>
    public List<TmdbEpisodeDto> Episodes { get; set; } = [];

    public List<TmdbMovieDto> Movies { get; set; } = [];
}

#endregion

#region TMDB (Models/TMDB/TmdbShow.cs + Models/TMDB/TmdbEpisode.cs)

/// <summary>
/// <c>Series.TMDB.Shows[]</c> entry and <c>GET /api/v3/Series/{id}/TMDB/Show</c> entry.
/// <c>AlternateOrderingID</c> is the ordering in use — the show id string when the default
/// ordering is in use, otherwise the alternate ordering's collection id.
/// </summary>
public sealed class TmdbShowDto
{
    public int ID { get; set; }

    public string AlternateOrderingID { get; set; } = "";

    public string Title { get; set; } = "";

    public List<TmdbOrderingDto>? Ordering { get; set; }
}

/// <summary>
/// <c>GET /api/v3/TMDB/Show/{showId}/Episode</c> entry (with <c>include=Ordering</c>) and
/// nested <c>Episode.TMDB.Episodes[]</c> entry.
/// </summary>
public sealed class TmdbEpisodeDto
{
    public int ID { get; set; }

    public int ShowID { get; set; }

    /// <summary>Ordering in use: show id string for the default ordering, else the alternate ordering id.</summary>
    public string AlternateOrderingID { get; set; } = "";

    public string Title { get; set; } = "";

    public int SeasonNumber { get; set; }

    public int EpisodeNumber { get; set; }

    /// <summary><c>include=Ordering</c>: all orderings of this TMDB episode.</summary>
    public List<TmdbOrderingDto>? Ordering { get; set; }
}

/// <summary><c>TmdbEpisode.OrderingInformation</c> / <c>TmdbShow.OrderingInformation</c>.</summary>
public sealed class TmdbOrderingDto
{
    public string OrderingID { get; set; } = "";

    public string OrderingName { get; set; } = "";

    public int SeasonNumber { get; set; }

    public int EpisodeNumber { get; set; }

    public bool IsDefault { get; set; }

    public bool IsPreferred { get; set; }

    public bool InUse { get; set; }
}

public sealed class TmdbMovieDto
{
    public int ID { get; set; }

    public string Title { get; set; } = "";
}

#endregion

#region File (Models/Shoko/File.cs + Models/Shoko/FileCrossReference.cs)

/// <summary>
/// <c>GET /api/v3/ManagedFolder/{id}/File</c> entry and <c>Episode.Files[]</c> entry.
/// <c>SeriesIDs</c> is populated with <c>include=XRefs</c>.
/// </summary>
public sealed class FileDto
{
    public int ID { get; set; }

    public long Size { get; set; }

    public bool IsVariation { get; set; }

    public bool IsIgnored { get; set; }

    public List<FileLocationDto> Locations { get; set; } = [];

    /// <summary>File/episode cross-references grouped per series (<c>include=XRefs</c>).</summary>
    public List<FileCrossRefGroupDto>? SeriesIDs { get; set; }
}

public sealed class FileLocationDto
{
    public int ID { get; set; }

    public int FileID { get; set; }

    public int ManagedFolderID { get; set; }

    /// <summary>Relative path from the managed folder root on the server.</summary>
    public string RelativePath { get; set; } = "";

    public bool IsAccessible { get; set; }
}

/// <summary><c>File.SeriesIDs[]</c> entry: one series and the episodes this file matches in it.</summary>
public sealed class FileCrossRefGroupDto
{
    public FileSeriesIdsDto SeriesID { get; set; } = new();

    public List<FileEpisodeIdsDto> EpisodeIDs { get; set; } = [];
}

public sealed class FileSeriesIdsDto
{
    /// <summary>Shoko series id (null when the series is not local yet).</summary>
    public int? ID { get; set; }

    public int AniDB { get; set; }
}

public sealed class FileEpisodeIdsDto
{
    /// <summary>Shoko episode id (null when the episode is not local yet).</summary>
    public int? ID { get; set; }

    public int AniDB { get; set; }

    public string ED2K { get; set; } = "";

    public long FileSize { get; set; }
}

#endregion

#region Common

/// <summary><c>Models/Common/Title.cs</c> (AniDB/TMDB titles list entry).</summary>
public sealed class TitleDto
{
    public string Name { get; set; } = "";

    /// <summary>Language code (e.g. "en", "ja").</summary>
    public string Language { get; set; } = "";

    public TitleType Type { get; set; }

    public bool Default { get; set; }

    public bool Preferred { get; set; }

    public string Source { get; set; } = "";
}

#endregion