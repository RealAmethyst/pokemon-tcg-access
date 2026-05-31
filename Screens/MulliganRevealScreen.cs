using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Util;

namespace PokemonTCGLiveAccessibility.Screens;

// Start-of-match MULLIGANS reveal popup. When a player's opening hand has no Basic Pokémon they must
// mulligan (reveal the hand, reshuffle, redraw); the resulting reveal — yours or the opponent's — is
// shown to the local player. Driver: MulliganController → MatchManager.mulliganOverlay
// (MulliganDisplayOverlay).Open(playerName, whichPlayer, mulligans, bottomSetup), once per owner.
//
// The popup is built through the generic ModalDrawer/ModalPopup system (a ModalElementContainer), so
// the mod's existing ModalContainerScreen ALREADY owns its navigation + the DONE button (that's why
// it was dismissable in-game). The only gap is reading the CONTENT: the revealed cards are CardUI
// graphics inside MulliganDrawGroups — NOT TMP text — so the generic modal scan can't speak them, and
// the player/count context isn't obvious. So this is a small ANNOUNCE-ONLY hook (no second router
// handler, so nothing to conflict with ModalContainerScreen): on Open we read the Mulligan data
// directly and speak a complete summary (who mulliganed, how many times, and the revealed card names).
// Names resolve the mod's usual way (MatchManager.TryGetCardName — the revealed cards are public). The
// announce runs as Open's postfix, i.e. AFTER ModalContainerScreen activates for the underlying modal,
// and interrupts it so the user hears one clean, complete readout; DONE stays handled by the modal.
//
// The postfix takes only `playerName` + `mulligans` (both unambiguous) and omits `whichPlayer` — its
// PlayerID type exists in two namespaces (SharedSDKUtils / MatchLogic), and the player's display NAME
// already reads clearly without resolving "you" vs "opponent".
internal static class MulliganRevealScreen
{
    private static ScreenRouter? _router;

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        _router = router;
        // Whole body guarded: an uncaught throw in a handler's Install aborts the rest of the mod's
        // OnInitializeMelon (that's what previously left BattleSettings unregistered). Never let this
        // one take others down with it.
        try
        {
            // MulliganDisplayOverlay has TWO methods named "Open": its own Open(string, PlayerID, List, …)
            // and the inherited parameterless BattleMenu.Open(). AccessTools.Method(type, "Open") throws
            // AmbiguousMatchException, so pick the multi-parameter one explicitly by arity.
            var open = AccessTools.GetDeclaredMethods(typeof(MulliganDisplayOverlay))
                .FirstOrDefault(m => m.Name == "Open" && m.GetParameters().Length >= 3);
            if (open == null) { Log.Error("MulliganRevealScreen: MulliganDisplayOverlay.Open(...) not found"); return; }
            harmony.Patch(open, postfix: new HarmonyMethod(AccessTools.Method(typeof(MulliganRevealScreen), nameof(Open_Postfix))));
            Log.Info("MulliganRevealScreen installed");
        }
        catch (Exception ex) { Log.Error($"MulliganRevealScreen.Install threw: {ex.Message}"); }
    }

    private static void Open_Postfix(string playerName, List<MulliganController.Mulligan> mulligans)
    {
        if (_router == null || mulligans == null) return;
        try
        {
            int n = mulligans.Count;
            string who = string.IsNullOrWhiteSpace(playerName) ? "A player" : Titleize(playerName);
            // Mirrors the popup's own line, e.g. "Professor had to take 1 mulligan before drawing a Basic Pokémon."
            var parts = new List<string>
            {
                "Mulligans",
                $"{who} had to take {n} mulligan{(n == 1 ? "" : "s")} before drawing a Basic Pokémon."
            };

            for (int i = 0; i < mulligans.Count; i++)
            {
                var names = ResolveNames(mulligans[i]);
                string label = n > 1 ? $"Mulligan {i + 1}: " : "";
                parts.Add(names.Count > 0 ? $"{label}{string.Join(", ", names)}" : $"{label}cards hidden");
            }

            _router.Speech.Say(string.Join(". ", parts), interrupt: true);
            Log.Info($"Mulligan reveal: player='{playerName}' count={n} cards=[{string.Join(" | ", mulligans.Select(m => string.Join(",", ResolveNames(m))))}]");
        }
        catch (Exception ex) { Log.Error($"MulliganRevealScreen.Open_Postfix threw: {ex.Message}"); }
    }

    // The revealed cards' localized names — resolved the exact way BattlePromptScreen.ResolveName does
    // (MatchManager.TryGetCardNameFromEntityID, localizedName:true), since the revealed cards are public.
    // Drops any that don't resolve rather than reading the "card" placeholder.
    private static List<string> ResolveNames(MulliganController.Mulligan m)
    {
        var names = new List<string>();
        if (m?.cardEntityIds == null) return names;
        var mm = MatchManager.GetInstanceFromScene();
        if (mm == null) return names;
        foreach (var eid in m.cardEntityIds)
        {
            if (string.IsNullOrEmpty(eid)) continue;
            try { if (mm.TryGetCardNameFromEntityID(eid, out var name, true) && !string.IsNullOrWhiteSpace(name)) names.Add(name); }
            catch { }
        }
        return names;
    }

    // The game passes the player's name uppercased ("PROFESSOR"); soften to "Professor" so it doesn't
    // read shouty. Leaves short all-caps tokens (<= 3 chars, likely acronyms) alone.
    private static string Titleize(string s)
    {
        s = s.Trim();
        if (s.Length <= 3) return s;
        if (s.ToUpperInvariant() != s) return s;   // not all-caps — leave as authored
        return char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();
    }
}
