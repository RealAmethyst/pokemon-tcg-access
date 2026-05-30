using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Util;
using TPCI.Rainier.FTUE;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Hooks;

// Bridges the game's FTUE (First Time User Experience / tutorial) state into the mod.
//
// Two questions screens want to ask:
//   1. "Is the FTUE running right now?" — answered by FTUEManager.runFTUE. When true, the
//      underlying screen shouldn't auto-announce its content because the FTUE will drive
//      speech via DialogueManager.StartDialogue or by focusing a specific button — and the
//      underlying screen's "Friends, 1 of 1" leak would compete with the welcome dialogue.
//   2. "Did the FTUE just highlight a specific button on my screen?" — answered by
//      FocusedElement. Each FTUE indicator (FTUESocialIndicator etc.) calls
//      MenuButtonController.Focus(elementPos), which reparents that button under a UI
//      blocker so only that button is clickable. Our keyboard nav has to follow.
//
// FocusedElement is updated by a Harmony postfix on the MenuButtonController.previousElement
// SETTER (not the individual Focus overloads). Every Focus path — the public Focus(int,…) AND
// the protected Focus(Selectable,List<…>,…) used for the Battle Pass tier reward — funnels
// through `previousElement = new FocusedElement(…)`, so hooking the setter catches them all.
// (Hooking only Focus(int,…) missed the multi-arg overload, so the tier-reward button never
// surfaced.) `previousElement.elements[0]` is the highlighted Selectable; we expose it directly.
// Clears come through Unfocus(). Screens query FTUEHook.FocusedElement each Tick / on resume.
internal static class FTUEHook
{
    public static Selectable? FocusedElement { get; private set; }
    public static event Action? FocusedElementChanged;

    public static bool FtueIsRunning
    {
        get
        {
            try
            {
                var mgr = FTUEManager.instance;
                return mgr != null && mgr.GuidedBehaviorSet?.RunningStrategy?.IsRunning == true;
            }
            catch
            {
                // ManagerSingleton may not be initialized yet (early boot, login screen)
                return false;
            }
        }
    }

    // We mirror FTUEManager._currentRunningStates by hooking AddToCurrentRunningStates /
    // RemoveFromCurrentRunningStates. That's the same source of truth the game uses for
    // "any FTUE state behavior is currently entered". Mirroring rather than reading via
    // reflection lets us classify by concrete subclass without per-call reflection cost.
    private static readonly List<object> _trackedStates = new();

    // True if any currently-entered FTUE state means "FTUE is busy / about to chatter".
    // Defaults to TRUE on any tracked state — preparation states like FTUESetupData,
    // FTUEUIBlocker, FTUEOpenScreen, FTUEDisableShopBack run for tens to hundreds of
    // milliseconds *before* an FTUEDialogue actually starts. Treating them as "passive"
    // (the prior bug) means the screen announces its content during this prep window and
    // the user hears "Friends, 1 of 1" before the welcome message arrives.
    //
    // The only states that genuinely hand control back to the player are explicit
    // wait-for-user states and the fire-and-forget analytics/checkpoint states. Anything
    // else — including timed delays, scene loads, UI blockers — should keep us silent.
    public static bool FtueSpeechClaimingStateActive
    {
        get
        {
            for (int i = 0; i < _trackedStates.Count; i++)
            {
                if (!IsPassiveWaitState(_trackedStates[i])) return true;
            }
            return false;
        }
    }

    // True while the FTUE is TEACHING the attack/ability overlay (FTUEAttackOverlayIndicator). That
    // state only points coaching arrows at elements or focuses the close button and waits for you to
    // CLOSE the overlay — none of its branches ever execute an attack. So the player must not be able
    // to invoke an attack/ability/retreat during it: the game doesn't guard the op (it assumes you
    // follow the coaching), so doing so desyncs and freezes the tutorial. The player reads + backs out.
    public static bool AttackOverlayTeachingActive
    {
        get
        {
            for (int i = 0; i < _trackedStates.Count; i++)
                if (_trackedStates[i] is FTUEAttackOverlayIndicator) return true;
            return false;
        }
    }

    // Deck-editor tutorial target. FTUEDeckEditorIndicator highlights a specific card (its focused
    // element — the count badge / card button — is reparented under a Blocker, so we can't walk to
    // its CardStubParts). But the indicator itself carries the target card id + element type, so the
    // deck-editor handler reads them to announce "the tutorial wants you to add X" and jump to it.
    private static readonly FieldInfo? _deckCardIdField = AccessTools.Field(typeof(FTUEDeckEditorIndicator), "cardId");
    private static readonly FieldInfo? _deckElementField = AccessTools.Field(typeof(FTUEDeckEditorIndicator), "element");

    public static bool DeckEditorFtueActive => FindTrackedDeckIndicator() != null;

    public static string? DeckEditorFtueCardId
    {
        get { var ind = FindTrackedDeckIndicator(); return ind != null ? _deckCardIdField?.GetValue(ind) as string : null; }
    }

    public static string? DeckEditorFtueElement
    {
        get { var ind = FindTrackedDeckIndicator(); return ind != null ? _deckElementField?.GetValue(ind)?.ToString() : null; }
    }

    private static object? FindTrackedDeckIndicator()
    {
        for (int i = 0; i < _trackedStates.Count; i++)
            if (_trackedStates[i] is FTUEDeckEditorIndicator) return _trackedStates[i];
        return null;
    }

    private static bool IsPassiveWaitState(object state)
    {
        // The "FTUE is now waiting for the user to do something" states. The animator parks
        // here between active phases — e.g. after the welcome-dialogue chain ends on Profile
        // the animator transitions to FTUEWaitForScreenLeave + FTUEAnalytics and just sits
        // until the user navigates to Decks/Play. Speaking over these is exactly what we want.
        if (state is FTUEWaitForScreenLeave) return true;
        if (state is FTUEWaitForMenu) return true;
        if (state is FTUEWaitForGameOver) return true;
        // Fire-and-forget transient states that don't claim speech or block input.
        if (state is FTUEAnalytics) return true;
        if (state is FTUECheckpoint) return true;
        return false;
    }

    public static void Install(HarmonyLib.Harmony harmony)
    {
        // Hook the previousElement SETTER — every Focus overload sets it, so this captures the
        // public Focus(int,…) AND the protected Focus(Selectable,List<…>,…) the Battle Pass tier
        // reward (ChallengeScreenButtonController, elementPos 8 / TierZero) routes through. The
        // old per-overload hook only saw Focus(int,…), so TierZero never surfaced.
        var setter = AccessTools.PropertySetter(typeof(MenuButtonController), "previousElement");
        if (setter == null)
        {
            Log.Error("FTUEHook: could not resolve MenuButtonController.previousElement setter");
            return;
        }
        harmony.Patch(setter, postfix: new HarmonyMethod(AccessTools.Method(typeof(FTUEHook), nameof(PreviousElement_Postfix))));

        // Unfocus()  — clears the FTUE highlight when the user clicks the focused button or
        // when an Indicator state exits without forwarding.
        var unfocus = AccessTools.Method(typeof(MenuButtonController), nameof(MenuButtonController.Unfocus), Type.EmptyTypes);
        if (unfocus != null)
            harmony.Patch(unfocus, postfix: new HarmonyMethod(AccessTools.Method(typeof(FTUEHook), nameof(Unfocus_Postfix))));

        // Mirror FTUEManager's running-states list. AddToCurrentRunningStates fires from
        // RainierStateMachineBehaviour.OnStateEnter (every animator state transition into a
        // behavior state); Remove fires from OnStateExit.
        var addState = AccessTools.Method(typeof(FTUEManager), nameof(FTUEManager.AddToCurrentRunningStates));
        if (addState != null)
            harmony.Patch(addState, postfix: new HarmonyMethod(AccessTools.Method(typeof(FTUEHook), nameof(AddState_Postfix))));

        var removeState = AccessTools.Method(typeof(FTUEManager), nameof(FTUEManager.RemoveFromCurrentRunningStates));
        if (removeState != null)
            harmony.Patch(removeState, postfix: new HarmonyMethod(AccessTools.Method(typeof(FTUEHook), nameof(RemoveState_Postfix))));

        Log.Info("FTUEHook installed");
    }

    private static void PreviousElement_Postfix(MenuButtonController __instance)
    {
        // The setter just ran; previousElement is the FocusedElement just built (or null).
        // FocusedElement.elements[0] is the primary Selectable (Button/Toggle/InputField).
        try
        {
            var prev = __instance.previousElement;
            var selectable = prev?.elements?.Count > 0 ? prev.elements[0] : null;
            // Ignore the transient null the game sets in ClearPrevious() right before each new
            // focus — real clears come through Unfocus(). Only react to an actual new element.
            if (selectable == null || selectable == FocusedElement) return;
            FocusedElement = selectable;
            Log.Info($"FTUE focus: {selectable.gameObject.name}  chain: {ParentChain(selectable.transform)}");
            FocusedElementChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"FTUEHook.PreviousElement_Postfix threw: {ex.Message}");
        }
    }

    // Names of the focused element's ancestors (a few levels up) — a cheap diagnostic for
    // figuring out what an unlabeled "Button" actually is when its own name isn't enough
    // (e.g. "Button  chain: ExpansionSelection(Clone) < SeriesGroup < …" tells us it's a set tile).
    private static string ParentChain(UnityEngine.Transform t)
    {
        var sb = new System.Text.StringBuilder();
        int depth = 0;
        for (var p = t != null ? t.parent : null; p != null && depth < 8; p = p.parent, depth++)
        {
            if (sb.Length > 0) sb.Append(" < ");
            sb.Append(p.gameObject.name);
        }
        return sb.ToString();
    }

    private static void Unfocus_Postfix()
    {
        if (FocusedElement == null) return;
        FocusedElement = null;
        Log.Info("FTUE focus cleared");
        FocusedElementChanged?.Invoke();
    }

    private static void AddState_Postfix(object state)
    {
        if (state == null || _trackedStates.Contains(state)) return;
        _trackedStates.Add(state);
        Log.Info($"FTUE state entered: {state.GetType().Name} (total={_trackedStates.Count})");
    }

    private static void RemoveState_Postfix(object state)
    {
        if (state == null) return;
        if (_trackedStates.Remove(state))
            Log.Info($"FTUE state exited: {state.GetType().Name} (total={_trackedStates.Count})");
    }
}
