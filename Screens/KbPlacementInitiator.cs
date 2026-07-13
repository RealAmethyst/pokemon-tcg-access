using System.Collections.Generic;
using System.Linq;
using TPCI.Rainier.Match.Cards;
using TPCI.Rainier.Match.Interactions.Feedback;
using TPCI.Rainier.Match.Targeting;

namespace PokemonTCGLiveAccessibility.Screens;

// A keyboard-driven, raycast-free ICardTargetInitiator. We hand this to the game's own
// CardTargetingHandler.TryTargetCard so hand-card placement runs through the EXACT native flow a
// sighted player triggers (purpose.RecordPlacement → MatchUserOperations + SetOwnership animation
// + any FTUE coaching), with our keyboard choosing the receiver instead of the mouse pointer.
//
// Modeled on the game's own ClickReleaseAutoPlacementInitiator (which the game already uses when
// tap-to-place is off, SelectionCardOwner.OnCardClicked), but the imminent receiver and the
// commit/cancel signals come from our flags rather than the pointer raycast / InputManager. This
// means it works regardless of the "TTP" feature flag and never touches the EventSystem.
internal sealed class KbPlacementInitiator : ICardTargetInitiator
{
    private readonly KbReceiverFinder _finder = new();

    // The receiver the player has navigated to. The game highlights it as "imminent" each frame
    // and commits onto it once the placement is armed.
    public void SetChosen(ICardPlacementReceiver? receiver) => _finder.Chosen = receiver;

    // Armed when the player presses Enter to drop. EDGE-TRIGGERED, like every native initiator:
    // CardPickupHandler and ClickReleaseAutoPlacementInitiator return InputManager.IsClickUp() (true
    // for exactly one frame), and MatchAutomationTargetingInitiator — the game's own programmatic
    // placement — consumes its flag as it reports it. ReceiverPlacementHandler polls this once per
    // iteration of its evaluation loop, and AutoTargetingHandler literally waits for it to go false
    // again (`while (_targetInitiator.ShouldAttemptPlacement()) yield return null;`), so a latched
    // "true" is a lie the game will believe for as long as it holds the initiator.
    private bool _armed;

    // Arm the placement for the evaluation loop's next exit check. Set the receiver via SetChosen
    // BEFORE the loop's pass that precedes it: the loop records the imminent receiver, yields, and
    // only then asks us — so the receiver it commits to is the one it saw in that same iteration.
    public void ArmPlacement() => _armed = true;

    public void Reset()
    {
        _armed = false;
        _finder.Chosen = null;
    }

    // Force the explicit-commit path — never auto-place on the first valid receiver. Matches
    // CardPickupHandler (the hand-drag path this stands in for), which is also false.
    public bool IsAutoPlacementSupported => false;

    public IPlacementReceiverIntersectionTester GetImminentReceiverFinder() => _finder;

    public bool ShouldAttemptPlacement()
    {
        if (!_armed) return false;
        _armed = false;
        return true;
    }

    public bool ShouldCancelTarget() => false;   // we cancel explicitly via CardTargetingHandler.ClearTarget()

    public void StartCardTargetedFeedback(ICard3D card) { }
    public void EndCardTargetedFeedback(ICard3D card) { }
    public IEnumerable<ITargetStatusChangedListener> GetAuxiliaryStatusChangedListeners() { yield break; }
    public InteractionPhase GetTargetCardImminentPlacementPhase() => InteractionPhase.Idle;
    public void ModifyElementsForPlacementEvaluation(ICard3D targetCard, List<ICardPlacementReceiver> allEligibleReceivers, bool isEvaluationRunning) { }

    // Yields exactly the player-chosen receiver (when it's among the game's valid receivers), so
    // the placement loop treats it as the one under the "cursor".
    private sealed class KbReceiverFinder : IPlacementReceiverIntersectionTester
    {
        public ICardPlacementReceiver? Chosen;

        public void FindAndPrioritizeImminentReceivers(
            IReadOnlyList<ICardPlacementReceiver> validReceivers,
            List<(float priority, ICardPlacementReceiver receiver)> imminentReceivers)
        {
            var chosen = Chosen;
            if (chosen != null && validReceivers.Contains(chosen))
                imminentReceivers.Add((0f, chosen));
        }
    }
}
