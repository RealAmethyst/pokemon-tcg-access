namespace PokemonTCGLiveAccessibility.Screens;

internal interface IScreenHandler
{
    string Name { get; }

    // Called when this handler is pushed on the stack for the first time. Discover/build state,
    // speak the screen title, focus the first item.
    void OnEnter();

    // Called when a child handler is popped off and this one is now top again. State (focus
    // index, item list) is preserved from before the push — re-announce the focused item but
    // don't re-speak the screen title (the user already knows what screen they're on).
    void OnResume();

    // Called when this handler is being paused (a child is pushed on top) OR fully popped.
    // Don't null out MonoBehaviour references here — keep state alive for a potential OnResume.
    void OnExit();

    // Called when the game window regains focus (user alt-tabs back from browser, etc.).
    // The handler may have activated, changed state, or popped sub-screens while we were silent.
    // Re-announce the full current state (title + focused item) without resetting focus position.
    // Distinct from OnResume: that's "child popped"; this is "user came back to the window".
    void RespeakAll();

    // A stable string that captures what the user would currently hear (screen + focused item).
    // Mod.OnUpdate snapshots this at focus-loss and compares on focus-regain; if unchanged,
    // we stay silent ("nothing happened while you were away, don't bother repeating"). Different
    // value = a screen pushed/popped, focus moved, or popup body changed → re-announce.
    string StateSignature();

    void Tick();
}
