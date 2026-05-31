namespace PokemonTCGLiveAccessibility.Navigation;

// A navigable section of a section-stack screen (e.g. HOME). Both KeyboardNavigator (flat
// Selectable groups: the top strip, the game-mode tabs, the bottom shortcuts) and
// LearningLabSection (the drill-down lesson list under the LEARN tab) implement this, so the
// screen handler can treat every section the same way: Tab cycles sections, and Up/Down at a
// section's row edge escapes to the adjacent section (preserving column).
//
// These are exactly the members HUBHomeScreen.Tick / OnEnter / OnResume call on a section. A
// KeyboardNavigator already satisfies all of them; LearningLabSection maps them onto its data
// drill-down (Index = item index, IsAtTopRow = first item, etc.).
internal interface INavSection
{
    int Index { get; }
    int Count { get; }
    bool IsAtTopRow { get; }
    bool IsAtBottomRow { get; }
    int CurrentColumn { get; }
    void FocusFirst(bool announce = true, bool interruptPrior = true);
    void FocusFirstRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true);
    void FocusLastRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true);
    // interrupt:false lets a caller queue the re-announce after a preamble it just spoke (e.g. a
    // modal body) instead of barging over it; defaults to true (barge in) for ordinary refocus.
    void RefocusCurrent(bool announce = true, bool interrupt = true);
    void Tick();
}
