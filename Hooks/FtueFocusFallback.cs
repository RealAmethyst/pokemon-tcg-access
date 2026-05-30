using System.Collections.Generic;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Screens;
using PokemonTCGLiveAccessibility.Util;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Hooks;

// Global fallback for FTUE-highlighted elements on screens we don't have a dedicated handler
// for yet (Battle Pass, Decks, Shop, …). During the tutorial the game reparents exactly one
// element under an input blocker so only it is clickable. A handled screen surfaces that via
// its own RebuildForFtue; but when no handler owns the screen the highlighted element would be
// keyboard-unreachable, leaving the player stuck (observed: FTUE pointed at QuestBtn on the
// Battle Pass screen with nothing to read or activate).
//
// This watches FTUEHook.FocusedElement every frame and, ONLY while no screen handler is active
// (so it never fights a real handler for speech or key input), presents that one element for
// keyboard focus + Enter. It deliberately does not join the ScreenRouter stack — it operates in
// the gap where the stack is empty, which keeps it free of stack-ordering hazards.
internal static class FtueFocusFallback
{
    private static ScreenRouter? _router;
    private static KeyboardNavigator? _nav;
    private static Selectable? _shown;

    public static void Install(ScreenRouter router)
    {
        _router = router;
        _nav = new KeyboardNavigator(router.Speech) { AllowTab = false, GroupName = "Tutorial" };
    }

    public static void Tick()
    {
        if (_router == null || _nav == null) return;

        // Only step in when no screen handler owns the screen. Handled screens drive their own
        // FTUE focus via RebuildForFtue; stepping in there would double-announce and double-read keys.
        if (_router.Active != null) { _shown = null; return; }

        var focus = FTUEHook.FtueIsRunning ? FTUEHook.FocusedElement : null;
        if (focus == null || !focus.isActiveAndEnabled) { _shown = null; return; }

        if (focus != _shown)
        {
            _shown = focus;
            _nav.SetItems(new List<Selectable> { focus });   // DefaultLabel now resolves friendly names / skips badges
            _nav.FocusFirst(announce: true, interruptPrior: true);
            Log.Info($"FTUE fallback presenting: {focus.gameObject.name}");
        }

        // Enter/Space → onClick.Invoke(); arrows are no-ops on a single item. KeyboardNavigator.Tick
        // gates on Application.isFocused itself.
        _nav.Tick();
    }
}
