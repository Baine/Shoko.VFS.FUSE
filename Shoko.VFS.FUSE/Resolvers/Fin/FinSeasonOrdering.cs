namespace Shoko.VFS.FUSE.Resolvers.Fin;

/// <summary>
/// Pure season-bucketing and episode-ordering engine.
/// All inputs are already-resolved; no I/O or API calls.
/// </summary>
public static class FinSeasonOrdering
{
    /// <summary>
    /// Produce immutable ordered buckets for every season in the input.
    /// <paramref name="baseSeasonNumbers"/> supplies the resolved base-season-number
    /// per season ID.  A missing key yields base 0; alternate episodes get base + 1.
    /// </summary>
    public static IReadOnlyList<FinSeasonOrderingResult> Build(
        IReadOnlyList<FinSeasonOrderingInput> seasons,
        IReadOnlyDictionary<string, int>? baseSeasonNumbers = null)
    {
        var seasonIdOrder = seasons.Select(s => s.SeasonId).ToList();
        var bases = baseSeasonNumbers ?? new Dictionary<string, int>();
        var results = new List<FinSeasonOrderingResult>(seasons.Count);

        foreach (var season in seasons)
        {
            bases.TryGetValue(season.SeasonId, out var seasonBase);
            var result = BuildSingleSeason(season, seasonIdOrder, seasonBase);
            results.Add(result);
        }

        return results;
    }

    private static FinSeasonOrderingResult BuildSingleSeason(
        FinSeasonOrderingInput season,
        List<string> seasonIdOrder,
        int seasonBase)
    {
        var episodes = season.Episodes;

        // Step 1: Initial sort — missing air date first, then air date, then
        // season-ID position, then episode type, then episode number.
        var sorted = episodes
            .OrderBy(e => !e.AiredAt.HasValue)
            .ThenBy(e => e.AiredAt)
            .ThenBy(e => seasonIdOrder.IndexOf(e.SeasonId))
            .ThenBy(e => e.Type)
            .ThenBy(e => e.AniDbEpisodeNumber)
            .ToList();

        // Step 2: Bucket into lists, skip hidden, apply conversions.
        var episodeList = new List<FinOrderingEpisode>();
        var altEpisodesList = new List<FinOrderingEpisode>();
        var specialsList = new List<FinOrderingEpisode>();
        var extrasList = new List<FinOrderingEpisode>();

        var specialsBeforeEpisodes = new HashSet<int>();
        var specialsAnchors = new Dictionary<int, FinOrderingEpisode>();

        int index = 0;
        int lastNormalIndex = -1;

        foreach (var ep in sorted)
        {
            if (ep.IsHidden)
            {
                // D1: canonical SeasonInfo does NOT advance `index` for hidden
                // episodes, so the following normal episode keeps the same
                // anchor position (SeasonInfo.cs:240-241 `continue` w/o index++).
                continue;
            }

            var effectiveType = ep.Type == FinEpisodeType.Episode
                && season.EpisodeConversion == SeriesEpisodeConversion.EpisodesAsSpecials
                ? FinEpisodeType.Special
                : ep.Type;

            switch (effectiveType)
            {
                case FinEpisodeType.Episode:
                    episodeList.Add(ep);
                    lastNormalIndex = index;
                    break;

                case FinEpisodeType.Other:
                    if (ep.ExtraType.HasValue)
                        extrasList.Add(ep);
                    else
                        altEpisodesList.Add(ep);
                    break;

                default:
                    if (ep.ExtraType.HasValue)
                    {
                        extrasList.Add(ep);
                    }
                    else if (effectiveType == FinEpisodeType.Special
                             && season.EpisodeConversion == SeriesEpisodeConversion.SpecialsAsEpisodes)
                    {
                        episodeList.Add(ep);
                        lastNormalIndex = index;
                    }
                    else if (effectiveType == FinEpisodeType.Special)
                    {
                        specialsList.Add(ep);
                        if (lastNormalIndex == -1)
                        {
                            specialsBeforeEpisodes.Add(ep.EpisodeId);
                        }
                        else
                        {
                            // Find the previous normal episode from sorted list
                            // between lastNormalIndex and current index.
                            var previous = sorted
                                .GetRange(lastNormalIndex, index - lastNormalIndex)
                                .FirstOrDefault(e =>
                                    e.Type == FinEpisodeType.Episode
                                    && season.EpisodeConversion != SeriesEpisodeConversion.EpisodesAsSpecials);
                            if (previous != null)
                                specialsAnchors[ep.EpisodeId] = previous;
                        }
                    }
                    break;
            }
            index++;
        }

        // Step 3: Final-sort non-extra buckets when OrderByAirdate=false.
        if (!season.OrderByAirdate)
        {
            episodeList = SortByNumber(episodeList, seasonIdOrder);
            altEpisodesList = SortByNumber(altEpisodesList, seasonIdOrder);
            specialsList = SortByNumber(specialsList, seasonIdOrder);
        }

        // Step 4: Movie→Web fallback — if main episodes empty but alts exist,
        // promote alts and recompute specials anchors. Mirrors canonical
        // SeasonInfo.cs:303-342: the type only flips to Web when the season
        // type is non-custom Movie; a hidden main movie with normal parts also
        // flips to Web.
        var wasPromotedToAlt = false;
        var type = season.Type;
        var isCustomType = season.IsCustomType;
        string? promotedSeriesType = null;

        if (episodeList.Count == 0 && altEpisodesList.Count > 0)
        {
            if (!isCustomType && type == FinSeriesType.Movie)
            {
                type = FinSeriesType.Web;
                promotedSeriesType = type.ToString();
            }

            wasPromotedToAlt = true;
            episodeList = altEpisodesList;
            altEpisodesList = [];

            // Recompute specials anchors against new episode list.
            specialsBeforeEpisodes.Clear();
            specialsAnchors.Clear();

            index = 0;
            lastNormalIndex = -1;
            foreach (var ep in sorted)
            {
                if (episodeList.Any(e => e.EpisodeId == ep.EpisodeId))
                {
                    lastNormalIndex = index;
                }
                else if (specialsList.Any(e => e.EpisodeId == ep.EpisodeId))
                {
                    if (lastNormalIndex == -1)
                    {
                        specialsBeforeEpisodes.Add(ep.EpisodeId);
                    }
                    else
                    {
                        var previous = sorted
                            .GetRange(lastNormalIndex, index - lastNormalIndex)
                            .FirstOrDefault(e => episodeList.Any(x => x.EpisodeId == e.EpisodeId));
                        if (previous != null)
                            specialsAnchors[ep.EpisodeId] = previous;
                    }
                }
                index++;
            }
        }
        else if (!isCustomType && type == FinSeriesType.Movie
                 && sorted.Any(e => e.IsMainEntry && e.IsHidden))
        {
            // D2: hidden main movie whose parts are normal (hidden) episodes
            // also switches to Web (SeasonInfo.cs:340-342).
            type = FinSeriesType.Web;
            promotedSeriesType = type.ToString();
        }

        // Step 5: SpecialsAsExtraFeaturettes — move specials + alts to extras.
        if (season.EpisodeConversion == SeriesEpisodeConversion.SpecialsAsExtraFeaturettes)
        {
            if (specialsList.Count > 0)
            {
                extrasList.AddRange(specialsList);
                specialsAnchors.Clear();
                specialsList = [];
            }
            if (altEpisodesList.Count > 0)
            {
                extrasList.AddRange(altEpisodesList);
                altEpisodesList = [];
            }
        }

        // Step 6: Assign episode numbers (1-based within each bucket).
        var placedEpisodes = AssignNumbers(episodeList, isSpecial: false, isExtra: false, isAlternate: false, seasonBase);
        var placedAlts = AssignNumbers(altEpisodesList, isSpecial: false, isExtra: false, isAlternate: true, seasonBase);
        var placedSpecials = AssignNumbers(specialsList, isSpecial: true, isExtra: false, isAlternate: false, seasonBase);
        var placedExtras = AssignNumbers(extrasList, isSpecial: false, isExtra: true, isAlternate: false, seasonBase);

        // Season number is the resolved base from the input mapping (0 if missing).
        var seasonNumber = seasonBase;

        return new FinSeasonOrderingResult(
            SeasonId: season.SeasonId,
            Episodes: placedEpisodes,
            AlternateEpisodes: placedAlts,
            Specials: placedSpecials,
            Extras: placedExtras,
            SeasonNumber: seasonNumber,
            WasPromotedToAlt: wasPromotedToAlt,
            PromotedSeriesType: promotedSeriesType,
            SeriesType: type);
    }

    private static List<FinOrderingEpisode> SortByNumber(
        List<FinOrderingEpisode> list,
        List<string> seasonIdOrder)
        => list
            .OrderBy(e => seasonIdOrder.IndexOf(e.SeasonId))
            .ThenBy(e => e.Type)
            .ThenBy(e => e.AniDbEpisodeNumber)
            .ToList();

    private static IReadOnlyList<FinPlacedEpisode> AssignNumbers(
        IReadOnlyList<FinOrderingEpisode> episodes,
        bool isSpecial,
        bool isExtra,
        bool isAlternate,
        int seasonBase)
    {
        var result = new List<FinPlacedEpisode>(episodes.Count);
        for (int i = 0; i < episodes.Count; i++)
        {
            result.Add(new FinPlacedEpisode(
                EpisodeId: episodes[i].EpisodeId,
                EpisodeNumber: i + 1,
                SeasonNumber: isAlternate ? seasonBase + 1 : seasonBase,
                IsSpecial: isSpecial,
                IsExtra: isExtra,
                IsAlternate: isAlternate));
        }
        return result;
    }
}
