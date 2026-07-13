using System;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Speech;
using PokemonTCGLiveAccessibility.Util;
using SharedLogicUtils.source.DataTypes.RankProgression.DataObjects;
using TPCI.Localization;

namespace PokemonTCGLiveAccessibility.Hooks;

// Post-match ranked progression — the rank-points bar fill + the rank-up / league-up stinger you hear
// returning to the HUB after a ranked match. Driven by RankedAnimController (the play-screen rank hex),
// fired once per match by HUBLeagueTowerController.RankUpSetLeagueProgress() (gated on
// SeasonRank.previousMatchExpDelta > 0, then immediately cleared, so it won't repeat on later visits).
// Sighted players watch the number tick up and, on a threshold cross, see a "New Rank!" /
// "Welcome to <league>!" banner with a music stinger (STG_RankUp / STG_LeagueUp); we voice the same.
//
//  - AnimateToExp(cfg, startExp, endExp): the bar sweep. Announce the points gained + new total. Ladder
//    points below the top league are gain-only, so a loss simply yields no sweep (nothing to say).
//  - Play(Mode): the milestone banner, fired mid-sweep when a rank/league threshold is crossed.
//    messageTxt already holds the localized headline ("Welcome to <league>!" is pre-formatted); for a
//    new rank the headline alone is just "New Rank!", so we append the new tier's name. Season-end
//    promotions reuse the same controller (SeasonEndRewardsSetter) — also a real league-up worth voicing.
internal static class RankUpHook
{
    private static SpeechEngine? _speech;

    public static void Install(HarmonyLib.Harmony harmony, SpeechEngine speech)
    {
        _speech = speech;

        var animate = AccessTools.Method(typeof(RankedAnimController), nameof(RankedAnimController.AnimateToExp));
        if (animate != null)
            harmony.Patch(animate, postfix: new HarmonyMethod(AccessTools.Method(typeof(RankUpHook), nameof(AnimateToExp_Postfix))));
        else Log.Error("RankUpHook: RankedAnimController.AnimateToExp not found");

        var play = AccessTools.Method(typeof(RankedAnimController), nameof(RankedAnimController.Play));
        if (play != null)
            harmony.Patch(play, postfix: new HarmonyMethod(AccessTools.Method(typeof(RankUpHook), nameof(Play_Postfix))));
        else Log.Error("RankUpHook: RankedAnimController.Play not found");

        Log.Info("RankUpHook installed");
    }

    private static void AnimateToExp_Postfix(uint startExp, uint endExp)
    {
        if (_speech == null || endExp <= startExp) return;
        uint gained = endExp - startExp;
        var text = $"Plus {gained} rank {(gained == 1 ? "point" : "points")}, {endExp} total";
        _speech.Say(text, interrupt: true);
        Log.Info($"Rank points: {text}");
    }

    private static void Play_Postfix(RankedAnimController __instance, RankedAnimController.Mode animMode)
    {
        if (_speech == null) return;
        try
        {
            var headline = LocOf(Field<LocText>(__instance, "messageTxt"));
            string? text = headline;

            // NEW_LEAGUE's headline is already "Welcome to <league>!"; NEW_RANK's is just "New Rank!",
            // so append the freshly-reached tier name (the screen shows it only on the emblem).
            if (animMode == RankedAnimController.Mode.NEW_RANK)
            {
                var rank = Field<RankData>(__instance, "_newRankData");
                var rankName = (rank != null) ? Loc(rank.rankName) : null;
                if (!string.IsNullOrWhiteSpace(rankName))
                    text = string.IsNullOrWhiteSpace(headline) ? rankName : $"{headline}, {rankName}";
            }

            if (string.IsNullOrWhiteSpace(text)) return;
            _speech.Say(text!, interrupt: false);   // queue after the points line (Play fires mid-sweep)
            Log.Info($"Rank milestone: {text}");
        }
        catch (Exception ex) { Log.Error($"RankUpHook.Play_Postfix threw: {ex.Message}"); }
    }

    private static string? LocOf(LocText? t)
    {
        var s = t != null ? t.CurrentText : null;
        return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
    }

    private static string? Loc(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var v = LocalizationHook.Get(id!);
        return (!string.IsNullOrWhiteSpace(v) && !v!.Contains("(Missing)")) ? v.Trim() : null;
    }

    private static T? Field<T>(object instance, string name) where T : class
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, flags);
            if (f != null) return f.GetValue(instance) as T;
        }
        return null;
    }
}
