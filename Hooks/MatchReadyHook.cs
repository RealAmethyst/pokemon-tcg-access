using HarmonyLib;
using PokemonTCGLiveAccessibility.Util;

namespace PokemonTCGLiveAccessibility.Hooks;

// Makes the game's own "is the board settled?" answer honest while a user operation is in flight.
//
// THE BUG THIS FIXES (tutorial battle 2, "Premium Power Pro" — reproduced 2026-07-09):
// The FTUE arms each coaching step with `FTUEMatchManager.BlockOtherCards`, which scans the card
// pool for the first Card3D whose **server-side** `cardInfo.currentPos` matches the zone the step
// wants. That field only changes when the operation comes back and `MatchInfo.UpdateWithResults`
// applies it. So between "you played the card" and "the server said where it went", the played card
// still reports `currentPos == HAND`.
//
// If the next `FTUECardIndicator` scans inside that window it latches the in-flight card. Worse, the
// card has already left the hand fan, so `component.owner is HandFanController` is false and the scan
// takes its final else-branch — `focusedCard = component`, the in-flight card itself. `FocusCard` then
// runs `Card3D.WaitUntilCardIsIdleInOwner`, which captures `expectedOwner` ONCE from the stale
// `currentPos` (= the hand) and spins `while (owner != expectedOwner)`. The card lands in the discard,
// so that loop never exits, `cardFocused` never becomes true, and the `UnfocusCard()` fired by
// `Card3D.SetOwner` during the move early-returns. `FTUEMatchManager.focusedCard` is now stuck on a
// discarded card forever: the step's `while (focusedCard != null)` never completes, so `NextState()`
// and `FTUEMatchUnlockActions.EnterCardFreePlay()` never run, and
// `DoesFTUEPermitCardTargetability` — the predicate wired into the real `TargetingHandler` — returns
// false for every card on the board. Mouse and keyboard alike are dead, and `BlockAll(true)` has
// already disabled the sidebar, so the player can't even concede.
//
// WHY IT ONLY HITS SOME PLAYERS:
// `FTUEMatchAnimationDelay` sits between the play and the next indicator and is supposed to cover the
// window: `if (!PerformingAnimations()) yield return waitCustomTicks; do { yield return null; } while
// (PerformingAnimations());`. But `PerformingAnimations()` is only true once operations are QUEUED —
// it knows nothing about one being in flight. On a fast machine the response lands inside that fixed
// ~1s wait, the gate then holds for the whole animation, and the next scan sees a settled board. On a
// slow one the response takes several seconds, the gate sees nothing, exits, and the scan races.
// Measured: 520 ms round-trip (healthy) vs 5,180 ms (deadlocked), same battle, same build.
//
// THE FIX — no timers, no state of our own, no parallel bookkeeping:
// The SDK already tracks this exactly. `MatchService.SendOperation` sets `MatchInfo.sendingMsg = true`
// (and `matchState = Processing`) for every user operation — UseCard, PlaceCard, AttachCard, Evolve,
// Retreat, EndTurn — and refuses to send another while it's set. `MatchOperationMessageHandler` clears
// it the moment the response is handled, immediately before `UpdateWithResults` applies the new card
// positions. That flag IS "an operation is in flight and the board is not settled". We just teach
// `PerformingAnimations()` to say so.
//
// WHY THIS IS SAFE: every caller of `PerformingAnimations()` is a `while (...)` wait loop — the FTUE
// state behaviours, `RainierStateMachineBehaviour`, `BoardRebuilder`. Nothing uses it to *enable* an
// action, so a true result can never unlock anything or desync the match; it can only make something
// wait a moment longer, which is the point. Operation dequeuing in `MatchNetworkParser.Update()` gates
// on `MatchManager.OpLock`, NOT on this, so holding it true cannot stall the operation stream. (That's
// why we don't take the OpLock instead: `ClientOperationLock` has no timeout, and a missed RemoveLock
// would brick the match.)
//
// As a bonus this also repairs `FTUECardIndicator`'s own guard,
// `while (PerformingAnimations() && matchState != WaitForInput)` — dead code until now, because during
// your turn `matchState` is `WaitForInput` and the first term was false anyway. With an operation in
// flight `SendOperation` has already set `matchState = Processing`, so both terms are true and the
// indicator waits before it scans.
internal static class MatchReadyHook
{
    public static void Install(HarmonyLib.Harmony harmony)
    {
        var performing = AccessTools.Method(typeof(MatchNetworkParser), nameof(MatchNetworkParser.PerformingAnimations));
        if (performing == null)
        {
            Log.Error("MatchReadyHook: MatchNetworkParser.PerformingAnimations not found");
            return;
        }
        harmony.Patch(performing, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(MatchReadyHook), nameof(PerformingAnimations_Postfix))));

        Log.Info("MatchReadyHook installed");
    }

    private static void PerformingAnimations_Postfix(ref bool __result)
    {
        if (__result) return;                       // already queued/animating — nothing to add
        if (OperationInFlight()) __result = true;   // sent, not yet answered → the board isn't settled
    }

    // The SDK's own in-flight flag. Read live; never cached, never mirrored.
    private static bool OperationInFlight()
    {
        try
        {
            var match = NetworkMatchController.currentMatchInfo;
            return match != null && match.sendingMsg;
        }
        catch { return false; }
    }
}
