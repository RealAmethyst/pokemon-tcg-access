using System.Collections.Generic;
using System.Text;
using RainierClientSDK.source.SeasonRank;
using TPCI.Localization;

namespace PokemonTCGLiveAccessibility.Util;

// The player's ranked status for the HOME rank hex — the same data the game shows there: the
// league/tier emblem, the "rank points" count, and the progress bar toward the next rank. Computed
// from the live season config (HUBLeagueTowerController.seasonConfigContent, a static) + SeasonRank
// statics, so it mirrors exactly what the game would display. league/rank names are loc IDs (the game
// sets them via LocText.SetTextByID), so we resolve them. Returns null until the season data has
// loaded (caller then falls back to a plain "Rank" label).
internal static class RankReader
{
    public static string? Status()
    {
        try
        {
            var cfg = HUBLeagueTowerController.seasonConfigContent?.leagueConfigContent;
            if (cfg == null) return null;

            uint exp = SeasonRank.exp;
            var league = cfg.GetLeagueDataByEXP(exp);
            var rank = cfg.GetRankByEXP(exp);

            // "Great Ball, Tier 3" — league, then the rank tier within it (skip the tier if it just
            // repeats the league name).
            var head = new List<string>(2);
            var leagueName = Loc(league?.leagueName);
            var rankName = Loc(rank?.rankName);
            if (!string.IsNullOrWhiteSpace(leagueName)) head.Add(leagueName!);
            if (!string.IsNullOrWhiteSpace(rankName) && rankName != leagueName) head.Add(rankName!);

            var sb = new StringBuilder(head.Count > 0 ? string.Join(", ", head) : "Rank");
            sb.Append(", ").Append(exp).Append(exp == 1 ? " rank point" : " rank points");

            // rank.expLimit is the cumulative exp to complete the current rank → the gap is "points to
            // the next rank" (the progress bar's remaining fill). Omitted at the top rank.
            if (rank != null && rank.expLimit > exp)
            {
                uint toNext = rank.expLimit - exp;
                sb.Append(", ").Append(toNext).Append(toNext == 1 ? " point to next rank" : " points to next rank");
            }
            return sb.ToString();
        }
        catch { return null; }
    }

    // Same readout for an ARBITRARY player (the VS screen shows both players' standing, not just our
    // own SeasonRank static): pass the exp + Elo snapshot from PlayerDetails. Mirrors what the game
    // renders on SeasonRankDetails — the top "competitive" league shows Elo, every other shows the
    // cumulative rank-points count. Omits the "points to next rank" tail (clutter for a matchup line).
    public static string? StatusFor(uint exp, uint competitiveElo)
    {
        try
        {
            var cfg = HUBLeagueTowerController.seasonConfigContent?.leagueConfigContent;
            if (cfg == null) return null;

            var league = cfg.GetLeagueDataByEXP(exp);
            var rank = cfg.GetRankByEXP(exp);
            var leagueName = Loc(league?.leagueName);
            var rankName = Loc(rank?.rankName);

            // The competitive league is the last one in the config; it displays Elo, not rank points.
            var leagues = cfg.leagueData;
            var top = (leagues != null && leagues.Count > 0) ? leagues[leagues.Count - 1] : null;
            bool isCompetitive = top != null && league != null &&
                (ReferenceEquals(league, top) || league.leagueName == top.leagueName);

            var head = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(leagueName)) head.Add(leagueName!);
            if (!string.IsNullOrWhiteSpace(rankName) && rankName != leagueName) head.Add(rankName!);

            var sb = new StringBuilder(head.Count > 0 ? string.Join(", ", head) : "Rank");
            if (isCompetitive)
                sb.Append(", ").Append(competitiveElo).Append(" Elo");
            else
                sb.Append(", ").Append(exp).Append(exp == 1 ? " rank point" : " rank points");
            return sb.ToString();
        }
        catch { return null; }
    }

    private static string? Loc(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try
        {
            var v = LocalizationManager.Instance?.GetString(id);
            if (!string.IsNullOrWhiteSpace(v) && !v!.Contains("(Missing)")) return v;
        }
        catch { }
        return null;
    }
}
