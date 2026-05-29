using System;
using System.Collections.Generic;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Speech;
using PokemonTCGLiveAccessibility.Util;
using RainierClientSDK;
using _Rainier.Scripts.BattleLog;

namespace PokemonTCGLiveAccessibility.Hooks;

// Passive play-by-play narrator. Voices the game's OWN on-screen battle-log text as each action
// happens — opponent's draws/attacks/damage/KOs/prizes and our own confirmed actions — which is
// exactly the "announce what's shown on screen, queued" model the design calls for.
//
// Source: `BattleLog` (_Rainier.Scripts.BattleLog.BattleLog) is the in-memory log data model, fed
// the entire match (independent of the log panel being open) with text already localized AND
// no-cheat-filtered — hidden cards render as "a card"/"a Pokémon", never their identity. We read
// `PlainTextDisplayString` (TTS-friendly) and hook the DATA layer (`AddMainEntries`), not the UI
// cells (which only exist while the panel is open and re-fire on scroll recycling).
//
// SYNC TO THE ON-SCREEN ACTION — the proper model:
// The log line for a move is COMPOSED at operation-parse time, which runs AHEAD of that move's
// animation (vs the CPU especially — results are instant but the board animates over ~1-2s). The
// game plays moves out of MatchNetworkParser's `processingOperations` queue one at a time; every
// MatchOperation calls `MatchNetworkParser.OnOperationComplete(this)` when its visuals finish, which
// fires the public static `onOperationComplete` (per move) and `onOperationBatchComplete` (queue
// empty). So: while moves are animating we BUFFER each composed line and release it when a real
// (non-wrapper) move's visuals complete — speech now lands WITH the action, never before it. When
// nothing is animating we speak immediately.
//
// Two things make this robust where a first attempt failed:
//  • The game NULLS `onOperationComplete`/`onOperationBatchComplete` in `ResetCachedStaticElements`
//    every match, so a one-time subscription gets wiped (that's why opponent narration went silent).
//    We re-subscribe via a postfix on that reset, so our handlers survive every match.
//  • The start/end-of-sequence wrappers (StartEndCapOperation / OpInputRequestOperation) also fire
//    completion; releasing on those would speak a move's line before the move animates, so we skip
//    them. `onOperationBatchComplete` flushes any remainder, so a line is never stranded or lost.
internal static class BattleLogHook
{
    private static SpeechEngine? _speech;
    private static readonly Queue<string> _pending = new();
    private static bool _opHooksReady;

    // Operations that bracket/await rather than visualize a logged move — don't release lines on these.
    private static readonly HashSet<string> _wrapperOps = new()
    {
        "StartEndCapOperation", "OpInputRequestOperation",
    };

    public static void Install(HarmonyLib.Harmony harmony, SpeechEngine speech)
    {
        _speech = speech;

        var addEntries = AccessTools.Method(
            typeof(_Rainier.Scripts.BattleLog.BattleLog),
            nameof(_Rainier.Scripts.BattleLog.BattleLog.AddMainEntries));
        if (addEntries == null)
        {
            Log.Error("BattleLogHook: could not resolve BattleLog.AddMainEntries");
            return;
        }
        harmony.Patch(addEntries, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(BattleLogHook), nameof(AddMainEntries_Postfix))));

        // Re-subscribe after the game clears the operation delegates each match (see class header).
        var reset = AccessTools.Method(typeof(MatchNetworkParser), nameof(MatchNetworkParser.ResetCachedStaticElements));
        if (reset != null)
            harmony.Patch(reset, postfix: new HarmonyMethod(AccessTools.Method(typeof(BattleLogHook), nameof(ResetCachedStaticElements_Postfix))));
        else
            Log.Error("BattleLogHook: could not resolve MatchNetworkParser.ResetCachedStaticElements");

        SubscribeOperationHooks();   // cover the current match if a reset already ran before us
        Log.Info("BattleLogHook installed");
    }

    private static void SubscribeOperationHooks()
    {
        try
        {
            // Remove-then-add so we're attached exactly once even if called repeatedly.
            MatchNetworkParser.onOperationComplete -= OnOperationComplete;
            MatchNetworkParser.onOperationComplete += OnOperationComplete;
            MatchNetworkParser.onOperationBatchComplete -= OnOperationBatchComplete;
            MatchNetworkParser.onOperationBatchComplete += OnOperationBatchComplete;
            _opHooksReady = true;
        }
        catch (Exception ex) { Log.Error($"BattleLogHook: op-hook subscribe threw: {ex.Message}"); }
    }

    private static void ResetCachedStaticElements_Postfix()
    {
        _pending.Clear();            // new match / cleanup — drop any stale buffered lines
        SubscribeOperationHooks();   // the reset just nulled the delegates; re-attach
    }

    private static void AddMainEntries_Postfix(IEnumerable<IBattleLogMainEntryData> newMainEntries)
    {
        if (newMainEntries == null) return;
        foreach (var entry in newMainEntries)
        {
            if (entry == null) continue;
            var text = entry.PlainTextDisplayString;
            if (string.IsNullOrWhiteSpace(text)) text = entry.DisplayString;
            Log.Info($"BattleLog entry [anim={AnimationsInFlight()} myTurn={IsMyTurn()}]: '{text}'");
            if (_speech == null || string.IsNullOrWhiteSpace(text)) continue;

            // Buffer only the OPPONENT's actions (sync them to the animation — you can't see them, and
            // the game composes their log ahead of the visual). On YOUR turn, speak immediately: you
            // just took the action so you have context, AND buffering would otherwise PUSH BACK a KO
            // attack's log — the game already holds that whole chain's log until you finish taking your
            // prize (the op isn't "Finished" until then), so it composes late; delaying it further put
            // it well after the prize. Immediate = it lands when it shows up. Drain any backlog first so
            // order holds. Fail-safe: if the op hooks couldn't attach, never buffer (immediate as before).
            if (_opHooksReady && AnimationsInFlight() && !IsMyTurn())
            {
                _pending.Enqueue(text!);
            }
            else
            {
                FlushPending();
                _speech.Say(text!, interrupt: false);
            }
        }
    }

    private static bool AnimationsInFlight()
    {
        try { return MatchNetworkParser.PerformingAnimations(); }
        catch { return false; }
    }

    // The game's own "is it the local player's turn" flag. Used to narrate our own actions immediately
    // (we have context + their KO-attack log is composed late by the game) vs syncing the opponent's.
    private static bool IsMyTurn()
    {
        try { return NetworkMatchController.currentMatchInfo?.myPlayerInfo?.isMyTurn ?? false; }
        catch { return false; }
    }

    private static void FlushPending()
    {
        if (_speech == null) return;
        while (_pending.Count > 0) _speech.Say(_pending.Dequeue(), interrupt: false);
    }

    // A move's visuals just finished. Release the lines buffered for it — but NOT on the bracketing
    // wrapper ops (releasing there would precede the actual move's animation). Releasing all currently
    // buffered lines here keeps multi-line moves (attack + knock out + prize) together at the moment
    // the move resolves on screen.
    private static void OnOperationComplete(MatchOperation done, MatchOperation next)
    {
        string type = done?.GetType().Name ?? "null";
        Log.Info($"OP complete: {type} (pending={_pending.Count})");
        if (_wrapperOps.Contains(type)) return;
        FlushPending();
    }

    // The whole sequence has played out — flush anything still buffered, in order (backstop).
    private static void OnOperationBatchComplete()
    {
        Log.Info($"OP batch complete (pending={_pending.Count})");
        FlushPending();
    }
}
