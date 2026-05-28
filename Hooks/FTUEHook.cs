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
// FocusedElement is updated by a Harmony postfix on MenuButtonController.Focus / Unfocus.
// `previousElement.elements[0]` is the highlighted Selectable (Button / Toggle / InputField);
// we expose it directly. Screens query FTUEHook.FocusedElement each Tick / on resume.
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
        // The virtual signature: Focus(int elementPos, FTUEGuidanceImage, string id, bool focusOnly, RainierScreenIndicator)
        var focus = AccessTools.Method(typeof(MenuButtonController), nameof(MenuButtonController.Focus),
            new[] { typeof(int), typeof(FTUEGuidanceImage), typeof(string), typeof(bool), typeof(RainierScreenIndicator) });
        if (focus == null)
        {
            Log.Error("FTUEHook: could not resolve MenuButtonController.Focus(int,FTUEGuidanceImage,string,bool,RainierScreenIndicator)");
            return;
        }
        harmony.Patch(focus, postfix: new HarmonyMethod(AccessTools.Method(typeof(FTUEHook), nameof(Focus_Postfix))));

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

    private static void Focus_Postfix(MenuButtonController __instance)
    {
        // After Focus runs the controller's previousElement is the FocusedElement just built.
        // FocusedElement.elements[0] is always the primary Selectable (Button/Toggle/InputField).
        try
        {
            var prev = __instance.previousElement;
            var selectable = prev?.elements?.Count > 0 ? prev.elements[0] : null;
            if (selectable == FocusedElement) return;
            FocusedElement = selectable;
            var label = selectable != null ? selectable.gameObject.name : "<null>";
            Log.Info($"FTUE focus: {label}");
            FocusedElementChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"FTUEHook.Focus_Postfix threw: {ex.Message}");
        }
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
