using System.Collections.Generic;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Speech;
using PokemonTCGLiveAccessibility.Util;
using _Rainier.Scripts.BattleLog;

namespace PokemonTCGLiveAccessibility.Hooks;

// Passive play-by-play narrator. Voices the game's OWN on-screen battle-log text as each action
// happens — opponent's draws/attacks/damage/KOs/prizes and our own confirmed actions — which is
// exactly the "announce what's shown on screen, queued" model the design calls for.
//
// Source: `BattleLog` (_Rainier.Scripts.BattleLog.BattleLog) is the in-memory log data model. It
// is fed continuously the entire match (independent of whether the player has the log panel open),
// and each entry arrives with its text already localized AND no-cheat-filtered — hidden cards are
// rendered as "a card"/"a Pokémon", never their identity, so this mirrors the visible log exactly.
// We read `PlainTextDisplayString` (the TTS-friendly rendering) rather than the rich-text variant.
//
// We hook the DATA layer (`AddMainEntries`), not the UI cells (`BattleLogEntry`) which only exist
// while the log panel is open and re-fire on scroll recycling. Each new top-level line is spoken
// once, queued (interrupt:false), so a burst of events for one operation is heard in full and in
// order. This is deliberately separate from BattleScreen: the log should narrate even while a
// decision-prompt / coaching child handler is on top of the stack.
internal static class BattleLogHook
{
    private static SpeechEngine? _speech;

    public static void Install(HarmonyLib.Harmony harmony, SpeechEngine speech)
    {
        _speech = speech;

        var target = AccessTools.Method(
            typeof(_Rainier.Scripts.BattleLog.BattleLog),
            nameof(_Rainier.Scripts.BattleLog.BattleLog.AddMainEntries));
        if (target == null)
        {
            Log.Error("BattleLogHook: could not resolve BattleLog.AddMainEntries");
            return;
        }
        harmony.Patch(target, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(BattleLogHook), nameof(AddMainEntries_Postfix))));
        Log.Info("BattleLogHook installed");
    }

    private static void AddMainEntries_Postfix(IEnumerable<IBattleLogMainEntryData> newMainEntries)
    {
        if (newMainEntries == null) return;
        foreach (var entry in newMainEntries)
        {
            if (entry == null) continue;
            // Prefer the plain-text rendering; fall back to the rich-text one (TextProcessor
            // strips its markup) if plain text is empty for this entry.
            var text = entry.PlainTextDisplayString;
            if (string.IsNullOrWhiteSpace(text)) text = entry.DisplayString;
            // DIAGNOSTIC: log every entry so we can confirm in the log file whether the battle
            // log is firing and what text it produces. Remove once narration is verified.
            Log.Info($"BattleLog entry: '{text}'");
            if (_speech == null || string.IsNullOrWhiteSpace(text)) continue;
            _speech.Say(text!, interrupt: false);
        }
    }
}
