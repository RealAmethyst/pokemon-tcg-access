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
    // and commits onto it once Commit flips true.
    public void SetChosen(ICardPlacementReceiver? receiver) => _finder.Chosen = receiver;

    // Flipped true (with a chosen receiver set) when the player presses Enter to drop.
    public bool Commit;

    public void Reset()
    {
        Commit = false;
        _finder.Chosen = null;
    }

    // Force the explicit-commit path — never auto-place on the first valid receiver.
    public bool IsAutoPlacementSupported => false;

    public IPlacementReceiverIntersectionTester GetImminentReceiverFinder() => _finder;

    public bool ShouldAttemptPlacement() => Commit;
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
